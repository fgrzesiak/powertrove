using FileKit.Pdf;
using FileKit.Tabular;
using UglyToad.PdfPig.Filters;
using UglyToad.PdfPig.Tokens;

namespace PowerTrove.Pack.Pdf.Layout;

internal sealed class BoundedPdfFilters(CancellationToken cancellationToken) : IFilterProvider
{
    private long _decoded;
    private readonly CancellationToken _ct = cancellationToken;
    public IReadOnlyList<IFilter> GetFilters(DictionaryToken dictionary)
    {
        if (!dictionary.TryGet(NameToken.Filter, out var filter)) return [];
        return GetNamedFilters(filter switch { NameToken name => [name], ArrayToken array => array.Data.Cast<NameToken>().ToArray(), _ => throw new TableLayoutException("Indirect PDF filter definitions are unsupported.") });
    }
    public IReadOnlyList<IFilter> GetNamedFilters(IReadOnlyList<NameToken> names)
    {
        if (names.Count > 8) throw new TableLayoutException("Too many PDF stream filters.");
        return names.Select(name => (IFilter)new BoundedFilter(this, name.Data)).ToArray();
    }
    public IReadOnlyList<IFilter> GetAllFilters() => GetNamedFilters([NameToken.FlateDecode, NameToken.Create("ASCIIHexDecode"), NameToken.Create("ASCII85Decode")]);
    private sealed class BoundedFilter(BoundedPdfFilters owner, string name) : IFilter
    {
        public bool IsSupported => name is "FlateDecode" or "Fl" or "ASCIIHexDecode" or "AHx" or "ASCII85Decode" or "A85";
        public Memory<byte> Decode(Memory<byte> input, DictionaryToken streamDictionary, IFilterProvider provider, int filterIndex)
        {
            owner._ct.ThrowIfCancellationRequested(); if (!IsSupported) throw new TableLayoutException("Unsupported PDF structural stream filter.");
            var dictionary = new PdfDictionary { ["Filter"] = new PdfName(name) };
            if (streamDictionary.TryGet(NameToken.DecodeParms, out var parameters))
            {
                if (parameters is ArrayToken array) parameters = filterIndex < array.Data.Count ? array.Data[filterIndex] : NullToken.Instance;
                if (parameters is not NullToken) dictionary["DecodeParms"] = Convert(parameters, 0);
            }
            long remaining = 128L * 1024 * 1024 - owner._decoded;
            if (remaining < 1) throw new TableLayoutException("PDF stream expansion budget exceeded.");
            byte[] decoded = PdfStreamDecoder.Decode(new(dictionary, input.ToArray()), Math.Min(16 * 1024 * 1024, remaining), owner._ct);
            owner._decoded += decoded.Length; return decoded;
        }
        private static PdfObject Convert(IToken token, int depth)
        {
            if (depth > 16) throw new TableLayoutException("PDF filter parameter nesting limit exceeded.");
            if (token is NumericToken number && double.IsFinite(number.Double) && number.Double == Math.Truncate(number.Double) && number.Double is >= int.MinValue and <= int.MaxValue) return new PdfInteger((long)number.Double);
            if (token is NameToken name) return new PdfName(name.Data);
            if (token is BooleanToken boolean) return PdfBoolean.Of(boolean.Data);
            if (token is NullToken) return PdfNull.Instance;
            if (token is ArrayToken array && array.Data.Count <= 32) return new PdfArray(array.Data.Select(t => Convert(t, depth + 1)));
            if (token is DictionaryToken dictionary && dictionary.Data.Count <= 32)
            { var value = new PdfDictionary(); foreach (var entry in dictionary.Data) value[entry.Key] = Convert(entry.Value, depth + 1); return value; }
            throw new TableLayoutException("Unsupported PDF stream parameter reference.");
        }
    }
}
