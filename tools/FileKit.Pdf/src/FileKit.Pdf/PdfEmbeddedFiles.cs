using System.Text;

namespace FileKit.Pdf;

public sealed record PdfEmbeddedFile(string Name, ReadOnlyMemory<byte> Data);
public static class PdfEmbeddedFiles
{
    /// <summary>Reads selected associated/name-tree file payloads in memory. Never extracts their names as paths.</summary>
    public static IReadOnlyList<PdfEmbeddedFile> Read(PdfDocument document, Func<string, bool> select, int maxFiles = 32, int maxBytes = 5 * 1024 * 1024, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); if (maxFiles is < 1 or > 100 || maxBytes is < 1 or > 16 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(maxFiles));
        var results = new List<PdfEmbeddedFile>(); var visited = new HashSet<PdfDictionary>(); var streams = new HashSet<PdfStream>(); int nodes = 0; long total = 0;
        Visit(document.ResolveDictionary(document.ResolveDictionary(document.Catalog["Names"])?["EmbeddedFiles"]), 0);
        if (document.Resolve(document.Catalog["AF"]) is PdfArray associated) foreach (var item in associated.Items) Add(item);
        return results.AsReadOnly();
        void Visit(PdfDictionary? tree, int depth)
        {
            ct.ThrowIfCancellationRequested(); if (tree is null) return;
            if (depth > 32 || ++nodes > 1000 || !visited.Add(tree)) throw new PdfFormatException("Embedded file tree is cyclic or excessive.");
            if (document.Resolve(tree["Names"]) is PdfArray names)
            {
                if (names.Count % 2 != 0 || names.Count > 200) throw new PdfFormatException("Invalid embedded file name tree.");
                for (int i = 1; i < names.Count; i += 2) Add(names[i]);
            }
            if (document.Resolve(tree["Kids"]) is PdfArray kids) foreach (var child in kids.Items) Visit(document.ResolveDictionary(child), depth + 1);
        }
        void Add(PdfObject? file)
        {
            ct.ThrowIfCancellationRequested(); var specification = document.ResolveDictionary(file); if (specification is null) throw new PdfFormatException("Invalid embedded file specification.");
            string name = Text(document.Resolve(specification["UF"]) as PdfString ?? document.Resolve(specification["F"]) as PdfString);
            if (!select(name)) return;
            var payloads = document.ResolveDictionary(specification["EF"]); var stream = document.Resolve(payloads?["UF"] ?? payloads?["F"]) as PdfStream ?? throw new PdfFormatException("Missing embedded file payload.");
            if (!streams.Add(stream)) return; if (results.Count >= maxFiles) throw new PdfLimitExceededException("Too many selected embedded files.");
            var dictionary = new PdfDictionary(); foreach (string key in new[] { "Filter", "DecodeParms" }) dictionary[key] = Resolve(stream.Dictionary[key], 0);
            byte[] data = PdfStreamDecoder.Decode(new(dictionary, stream.Data), maxBytes, ct);
            if ((total += data.Length) > 16 * 1024 * 1024) throw new PdfLimitExceededException("Embedded file expansion budget exceeded."); results.Add(new(name, data));
        }
        PdfObject? Resolve(PdfObject? value, int depth)
        {
            if (depth > 16) throw new PdfLimitExceededException("Embedded filter parameter depth exceeded."); value = document.Resolve(value);
            if (value is PdfArray array) return new PdfArray(array.Items.Select(i => Resolve(i, depth + 1) ?? PdfNull.Instance));
            if (value is PdfDictionary dictionary) { var copy = new PdfDictionary(); foreach (var pair in dictionary.Entries) copy[pair.Key] = Resolve(pair.Value, depth + 1); return copy; }
            return value;
        }
    }
    private static string Text(PdfString? value)
    {
        if (value is null || value.Value.Length > 4096) throw new PdfFormatException("Invalid embedded file name."); var bytes = value.Value;
        if (bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff })) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe })) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        return Encoding.Latin1.GetString(bytes);
    }
}
