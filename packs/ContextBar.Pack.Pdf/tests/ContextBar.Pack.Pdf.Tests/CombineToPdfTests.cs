using System.Buffers.Binary;
using System.IO.Compression;
using ContextBar.Infrastructure.Files;
using ContextBar.Pack.Pdf;
using FileKit.Pdf;

namespace ContextBar.Pack.Pdf.Tests;

public sealed class CombineToPdfTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private static IAction Action(IImageToJpegConverter? converter = null) =>
        new PdfPack(FileActionServices.CreatePhysical(new FileSignatureVerifier()), imageConverter: converter).Actions
            .Single(a => a.Descriptor.Id == CombineToPdfAction.ActionId);

    private string Fixture(string fixture, string name) =>
        _dir.Write(name, File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture)));

    [Fact]
    public async Task Combine_PdfsAndImages_InFileNameOrder()
    {
        string b = Fixture("linked-objstm.pdf", "b report.pdf");   // 3 pages
        string a = Fixture("photo.jpg", "a cover.jpg");            // 1 page
        string d = _dir.Write("d chart.png", Png(4, 3));          // 1 page
        string c = Fixture("linked-objstm.pdf", "c appendix.pdf"); // 3 pages

        var receipt = await Action().ExecuteAsync(Snapshots.ForPaths(d, c, b, a), new EmptyParameters(), null, CancellationToken.None);

        Assert.True(receipt.IsSuccess, receipt.Message);
        Assert.Equal(_dir.File("a cover (combined).pdf"), Assert.Single(receipt.OutputPaths));
        var result = PdfDocument.Open(receipt.OutputPaths[0]);
        Assert.Equal(8, result.PageCount);
        Assert.False(result.WasRepaired);
        // Order: image (A4), 3 PDF pages, 3 PDF pages, image (A4).
        Assert.True(IsA4(result, 0), "First page is the cover image on A4 (orientation follows the image).");
        Assert.Equal(Width(PdfDocument.Open(b), 0), Width(result, 1), 1);
        Assert.True(IsA4(result, 7), "Last page is the PNG on A4.");
    }


    [Theory]
    [InlineData("name", 0)] // image (A4 width)
    [InlineData("pdfs-first", 1)] // PDF page (width of linked-objstm.pdf)
    [InlineData("images-first", 0)] // image (A4 width)
    public async Task Combine_PdfsAndImages_RespectsOrder(string order, int expectedFirstPageWidthMatch)
    {
        string b = Fixture("linked-objstm.pdf", "b report.pdf");   // 3 pages
        string a = Fixture("photo.jpg", "a cover.jpg");            // 1 page
        string d = _dir.Write("d chart.png", Png(4, 3));          // 1 page
        string c = Fixture("linked-objstm.pdf", "c appendix.pdf"); // 3 pages

        var parameters = new DictionaryParameters(new Dictionary<string, string> { { "Order", order } });
        var receipt = await Action().ExecuteAsync(Snapshots.ForPaths(d, c, b, a), parameters, null, CancellationToken.None);

        Assert.True(receipt.IsSuccess, receipt.Message);

        // Output file base name matches the first file in the sorted list, which depends on order
        // Let's just check the single output path
        var result = PdfDocument.Open(receipt.OutputPaths[0]);
        Assert.Equal(8, result.PageCount);

        if (expectedFirstPageWidthMatch == 0)
            Assert.True(IsA4(result, 0), "First page should be A4 image.");
        else
            Assert.Equal(Width(PdfDocument.Open(b), 0), Width(result, 0), 1);

        if (order == "images-first") {
            Assert.True(IsA4(result, 1), "Second page should also be an image.");
        } else if (order == "pdfs-first") {
            Assert.True(IsA4(result, 6), "Seventh page should be an image.");
        }
    }

    [Fact]
    public async Task ImageFormatWithoutConverter_FailsWithoutOutput()
    {
        string pdf = Fixture("linked-objstm.pdf", "a.pdf");
        string webp = _dir.Write("b.webp", WebPHeader());

        var receipt = await Action().ExecuteAsync(Snapshots.ForPaths(pdf, webp), new EmptyParameters(), null, CancellationToken.None);

        Assert.False(receipt.IsSuccess);
        Assert.Empty(_dir.NewFiles(pdf, webp));
    }

    [Fact]
    public async Task ImageFormatWithConverter_UsesThePort()
    {
        string pdf = Fixture("linked-objstm.pdf", "a.pdf");
        string webp = _dir.Write("b.webp", WebPHeader());
        var converter = new FakeConverter(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "photo.jpg")));

        var receipt = await Action(converter).ExecuteAsync(Snapshots.ForPaths(pdf, webp), new EmptyParameters(), null, CancellationToken.None);

        Assert.True(receipt.IsSuccess, receipt.Message);
        Assert.Equal(new[] { "webp" }, converter.Converted);
        Assert.Equal(4, PdfDocument.Open(receipt.OutputPaths[0]).PageCount);
    }

    [Fact]
    public void Fit_IsIdealOnlyWhenPdfsAndImagesAreBothSelected()
    {
        var requirements = Action().Descriptor.Selection!;

        Assert.Equal(SelectionFit.Ideal, SelectionRequirements.Evaluate(requirements, Snapshots.ForPaths("a.pdf", "b.jpg")));
        Assert.Equal(SelectionFit.Acceptable, SelectionRequirements.Evaluate(requirements, Snapshots.ForPaths("a.jpg", "b.png")));
        Assert.Equal(SelectionFit.Acceptable, SelectionRequirements.Evaluate(requirements, Snapshots.ForPaths("a.pdf", "b.pdf")));
        Assert.Equal(SelectionFit.None, SelectionRequirements.Evaluate(requirements, Snapshots.ForPaths("a.pdf")));
    }

    [Fact]
    public void Title_IsLocalized()
    {
        var original = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal("Zu einer PDF kombinieren", Action().Descriptor.Title);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentUICulture = original;
        }
    }

    private static double Width(PdfDocument document, int page) => PdfTools.GetPageSize(document, page).Width;

    private static bool IsA4(PdfDocument document, int page)
    {
        var (w, h) = PdfTools.GetPageSize(document, page);
        return Math.Abs(Math.Min(w, h) - 595.28) < 1 && Math.Abs(Math.Max(w, h) - 841.89) < 1;
    }

    /// <summary>RIFF/WEBP header: enough for signature detection; the fake converter ignores pixels.</summary>
    private static byte[] WebPHeader() =>
        "RIFF"u8.ToArray().Concat(new byte[] { 26, 0, 0, 0 }).Concat("WEBPVP8 "u8.ToArray()).Concat(new byte[18]).ToArray();

    private static byte[] Png(int width, int height)
    {
        var raw = new MemoryStream();
        for (int y = 0; y < height; y++)
        {
            raw.WriteByte(0);
            for (int x = 0; x < width; x++) raw.Write(new byte[] { 200, 40, 40 });
        }
        using var png = new MemoryStream();
        png.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)height);
        ihdr[8] = 8; ihdr[9] = 2;
        Chunk(png, "IHDR", ihdr);
        var z = new MemoryStream();
        using (var zl = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true)) zl.Write(raw.ToArray());
        Chunk(png, "IDAT", z.ToArray());
        Chunk(png, "IEND", Array.Empty<byte>());
        return png.ToArray();
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(len, (uint)data.Length); s.Write(len);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type); s.Write(typeBytes); s.Write(data);
        s.Write(new byte[4]); // CRC is not validated by the reader
    }

    private sealed class FakeConverter : IImageToJpegConverter
    {
        private readonly byte[] _jpeg;
        public FakeConverter(byte[] jpeg) => _jpeg = jpeg;
        public List<string> Converted { get; } = new();
        public bool CanConvert(string format) => format == "webp";
        public Task ConvertAsync(string path, string format, Stream jpeg, CancellationToken cancellationToken)
        {
            Converted.Add(format);
            return jpeg.WriteAsync(_jpeg, cancellationToken).AsTask();
        }
    }
}
