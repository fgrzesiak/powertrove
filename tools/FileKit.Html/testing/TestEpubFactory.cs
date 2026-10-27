using System.IO.Compression;
using System.Text;

namespace FileKit.Html.Testing;

/// <summary>Generated test data, linked into tests only. No binary fixtures.</summary>
internal static class TestEpubFactory
{
    internal static byte[] Create(string first = "<h1>First chapter</h1><p>Grüße</p>", string second = "<h1>Second chapter</h1>", string css = "h1{color:red}", byte[]? image = null, Action<Dictionary<string, byte[]>>? edit = null)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["mimetype"] = Text("application/epub+zip"),
            ["META-INF/container.xml"] = Text("<container xmlns='urn:oasis:names:tc:opendocument:xmlns:container' version='1.0'><rootfiles><rootfile full-path='OPS/package.opf' media-type='application/oebps-package+xml'/></rootfiles></container>"),
            ["OPS/package.opf"] = Text("<package xmlns='http://www.idpf.org/2007/opf' version='3.0' unique-identifier='book'><metadata/><manifest><item id='one' href='chapters/one.xhtml' media-type='application/xhtml+xml'/><item id='two' href='chapters/two.xhtml' media-type='application/xhtml+xml'/><item id='style' href='styles/book.css' media-type='text/css'/>" + (image is null ? "" : "<item id='image' href='images/blue.png' media-type='image/png'/>") + "</manifest><spine><itemref idref='two'/><itemref idref='one'/></spine></package>"),
            ["OPS/chapters/one.xhtml"] = Text(Chapter(first)), ["OPS/chapters/two.xhtml"] = Text(Chapter(second)), ["OPS/styles/book.css"] = Text(css)
        };
        if (image is not null) files["OPS/images/blue.png"] = image;
        edit?.Invoke(files);
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            foreach (var file in files.OrderBy(f => f.Key == "mimetype" ? 0 : 1))
            {
                using var stream = zip.CreateEntry(file.Key, file.Key == "mimetype" ? CompressionLevel.NoCompression : CompressionLevel.Optimal).Open(); stream.Write(file.Value);
            }
        }
        return output.ToArray();
    }
    internal static byte[] Text(string text) => Encoding.UTF8.GetBytes(text);
    private static string Chapter(string body) => "<html xmlns='http://www.w3.org/1999/xhtml' lang='de'><head><title>Test</title><link rel='stylesheet' href='../styles/book.css'/></head><body>" + body + "</body></html>";
}
