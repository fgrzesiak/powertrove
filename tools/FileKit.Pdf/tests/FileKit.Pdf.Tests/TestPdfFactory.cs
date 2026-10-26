using System.IO.Compression;
using System.Text;

namespace FileKit.Pdf.Tests;

/// <summary>Builds small, hand-written PDFs with classic cross-reference tables for tests.</summary>
internal static class TestPdfFactory
{
    public sealed record Options
    {
        public int Pages { get; init; } = 3;
        public bool NestedPageTree { get; init; }
        public int InheritedRotate { get; init; }
        public bool WithMetadata { get; init; } = true;
        public bool LinkFirstToLast { get; init; }
        public string? ExtraTrailerEntries { get; init; }
    }

    public static byte[] Create(Options? options = null)
    {
        options ??= new Options();
        var objects = new List<string>();
        // 1 catalog, 2 root pages, 3 font, 4 info, 5 metadata, then per page: page, content
        int pageBase = 6;
        var pageRefs = Enumerable.Range(0, options.Pages).Select(i => pageBase + i * 2).ToList();

        string catalog = "<< /Type /Catalog /Pages 2 0 R" + (options.WithMetadata ? " /Metadata 5 0 R" : "") + " >>";
        objects.Add(catalog);

        string rotate = options.InheritedRotate != 0 ? $" /Rotate {options.InheritedRotate}" : "";
        if (options.NestedPageTree && options.Pages >= 2)
        {
            int middleNumber = pageBase + options.Pages * 2;
            var firstHalf = pageRefs.Take(1).ToList();
            var secondHalf = pageRefs.Skip(1).ToList();
            objects.Add($"<< /Type /Pages /Kids [{Refs(firstHalf)} {middleNumber} 0 R] /Count {options.Pages} /MediaBox [0 0 595 842]{rotate} /Resources << /Font << /F1 3 0 R >> >> >>");
            _middle = $"<< /Type /Pages /Parent 2 0 R /Kids [{Refs(secondHalf)}] /Count {secondHalf.Count} >>";
            _parents = pageRefs.Select((p, i) => i == 0 ? 2 : middleNumber).ToList();
        }
        else
        {
            objects.Add($"<< /Type /Pages /Kids [{Refs(pageRefs)}] /Count {options.Pages} /MediaBox [0 0 595 842]{rotate} /Resources << /Font << /F1 3 0 R >> >> >>");
            _middle = null;
            _parents = pageRefs.Select(_ => 2).ToList();
        }

        objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        objects.Add("<< /Title (Secret Title) /Author (Secret Author) /Producer (TestFactory) >>");
        objects.Add(StreamObject("/Type /Metadata /Subtype /XML", "<x:xmpmeta xmlns:x='adobe:ns:meta/'>secret</x:xmpmeta>"));

        for (int i = 0; i < options.Pages; i++)
        {
            string annots = options.LinkFirstToLast && i == 0
                ? $" /Annots [<< /Type /Annot /Subtype /Link /Rect [0 0 100 100] /Dest [{pageRefs[^1]} 0 R /Fit] >>]"
                : "";
            objects.Add($"<< /Type /Page /Parent {_parents![i]} 0 R /Contents {pageRefs[i] + 1} 0 R{annots} >>");
            objects.Add(StreamObject("", $"BT /F1 24 Tf 72 720 Td (Page {i + 1}) Tj ET"));
        }

        if (_middle is not null) objects.Add(_middle);

        return Serialize(objects, "/Root 1 0 R /Info 4 0 R" + (options.ExtraTrailerEntries ?? ""));
    }

    [ThreadStatic] private static string? _middle;
    [ThreadStatic] private static List<int>? _parents;

    public static string StreamObject(string dictEntries, string content, bool compress = false)
    {
        if (!compress)
            return $"<< {dictEntries} /Length {Encoding.Latin1.GetByteCount(content)} >>\nstream\n{content}\nendstream";

        byte[] compressed = Zlib(Encoding.Latin1.GetBytes(content));
        return $"<< {dictEntries} /Filter /FlateDecode /Length {compressed.Length} >>\nstream\n{Encoding.Latin1.GetString(compressed)}\nendstream";
    }

    public static byte[] Zlib(byte[] data)
    {
        using var output = new MemoryStream();
        using (var z = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data);
        return output.ToArray();
    }

    /// <summary>Serializes objects 1..n with a classic xref table.</summary>
    public static byte[] Serialize(IReadOnlyList<string> objects, string trailerEntries)
    {
        var sb = new StringBuilder("%PDF-1.4\n%âãÏÓ\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(Encoding.Latin1.GetByteCount(sb.ToString()));
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        int xref = Encoding.Latin1.GetByteCount(sb.ToString());
        sb.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f\r\n");
        foreach (int offset in offsets) sb.Append($"{offset:D10} 00000 n\r\n");
        sb.Append($"trailer\n<< /Size {objects.Count + 1} {trailerEntries} >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    /// <summary>Appends an incremental update that replaces object <paramref name="number"/>.</summary>
    public static byte[] AppendUpdate(byte[] original, int number, string newObject, int size, string trailerEntries)
    {
        string text = Encoding.Latin1.GetString(original);
        int previousXref = int.Parse(text[(text.LastIndexOf("startxref", StringComparison.Ordinal) + 9)..].Trim().Split('\n')[0]);
        var sb = new StringBuilder(text);
        int offset = Encoding.Latin1.GetByteCount(sb.ToString());
        sb.Append($"{number} 0 obj\n{newObject}\nendobj\n");
        int xref = Encoding.Latin1.GetByteCount(sb.ToString());
        sb.Append($"xref\n{number} 1\n{offset:D10} 00000 n\r\n");
        sb.Append($"trailer\n<< /Size {size} /Prev {previousXref} {trailerEntries} >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    public static string ExtractText(PdfDocument document, int pageIndex)
    {
        var page = document.Pages[pageIndex];
        var contents = document.Resolve(page.Dictionary["Contents"]);
        var streams = contents switch
        {
            PdfStream s => new[] { s },
            PdfArray a => a.Items.Select(i => document.Resolve(i)).OfType<PdfStream>().ToArray(),
            _ => Array.Empty<PdfStream>()
        };
        return string.Concat(streams.Select(s =>
            s.Dictionary.GetName("Filter") == "FlateDecode"
                ? Encoding.Latin1.GetString(Inflate(s.Data))
                : Encoding.Latin1.GetString(s.Data)));
    }

    private static byte[] Inflate(byte[] data)
    {
        using var z = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress);
        using var output = new MemoryStream();
        z.CopyTo(output);
        return output.ToArray();
    }

    private static string Refs(IEnumerable<int> numbers) => string.Join(" ", numbers.Select(n => $"{n} 0 R"));
}
