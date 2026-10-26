using System.Globalization;
using ContextBar.Abstractions.Office;
using ContextBar.Pack.Pdf;
using FileKit.Pdf;

namespace ContextBar.Pack.Pdf.Tests;

public sealed class OfficeCombineTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    public void Dispose() => _dir.Dispose();
    private static IAction Action(IOfficeConverter? converter) => new PdfPack(FileActionServices.CreatePhysical(new Verifier()), officeConverter: converter).Actions.Single(a => a.Descriptor.Id == CombineToPdfAction.ActionId);

    [Theory]
    [InlineData("name", 400, 500, 595.28)]
    [InlineData("pdfs-first", 500, 400, 595.28)]
    [InlineData("images-first", 595.28, 400, 500)]
    public async Task OfficePdfImage_OrderAndTemporaryCleanup(string order, double first, double second, double third)
    {
        string doc = _dir.Write("a.docx", "fake Office input");
        string pdf = _dir.Write("b.pdf", Page(500));
        string image = _dir.Write("c.jpg", File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "photo.jpg")));
        byte[] before = File.ReadAllBytes(doc);
        var converter = new Converter();
        var receipt = await Action(converter).ExecuteAsync(Snapshots.ForPaths(image, pdf, doc), Snapshots.Parameters(("Order", order)), null, CancellationToken.None);
        Assert.True(receipt.IsSuccess, receipt.Message);
        var result = PdfDocument.Open(Assert.Single(receipt.OutputPaths));
        Assert.Equal(3, result.PageCount);
        double[] expected = { first, second, third };
        for (int i = 0; i < expected.Length; i++)
        {
            var size = PdfTools.GetPageSize(result, i);
            Assert.Equal(expected[i], Math.Min(size.Width, size.Height), 1);
        }
        Assert.Equal(before, File.ReadAllBytes(doc));
        Assert.False(Directory.Exists(Path.GetDirectoryName(Assert.Single(converter.Targets))));
        Assert.Equal(SelectionFit.Ideal, SelectionRequirements.Evaluate(Action(converter).Descriptor.Selection!, Snapshots.ForPaths(doc, pdf)));
        Assert.Equal(SelectionFit.Acceptable, SelectionRequirements.Evaluate(Action(converter).Descriptor.Selection!, Snapshots.ForPaths(pdf, "other.pdf")));
    }

    [Fact]
    public async Task MissingOffice_BlocksWholeSelectionAndExplainsWhy()
    {
        string doc = _dir.Write("a.docx", "fake"); string pdf = _dir.Write("b.pdf", Page(500));
        var action = Action(null);
        var preview = await action.PreviewAsync(Snapshots.ForPaths(doc, pdf), new EmptyParameters(), CancellationToken.None);
        Assert.True(preview.IsBlocked); Assert.Empty(preview.ExpectedOutputs); Assert.Contains(preview.Warnings, w => w.Contains("Office", StringComparison.Ordinal));
        var receipt = await action.ExecuteAsync(Snapshots.ForPaths(doc, pdf), new EmptyParameters(), null, CancellationToken.None);
        Assert.False(receipt.IsSuccess); Assert.Empty(receipt.OutputPaths); Assert.Empty(_dir.NewFiles(doc, pdf));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConversionFailureOrCancellation_CleansTemporaryFiles(bool cancel)
    {
        string doc = _dir.Write("a.docx", "fake"); string pdf = _dir.Write("b.pdf", Page(500));
        using var source = new CancellationTokenSource();
        var converter = new Converter { Fail = !cancel, Cancel = cancel ? source : null };
        var receipt = await Action(converter).ExecuteAsync(Snapshots.ForPaths(doc, pdf), new EmptyParameters(), null, source.Token);
        Assert.False(receipt.IsSuccess); Assert.Equal(cancel, receipt.IsCanceled);
        Assert.Empty(receipt.OutputPaths); Assert.Empty(_dir.NewFiles(doc, pdf));
        Assert.False(Directory.Exists(Path.GetDirectoryName(Assert.Single(converter.Targets))));
        Assert.DoesNotContain("private-data", string.Join(" ", receipt.Failures.Select(f => f.ErrorMessage)));
    }

    [Fact]
    public async Task GermanMissingOfficeWarning_IsLocalized()
    {
        var old = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("de-DE");
            var preview = await Action(null).PreviewAsync(Snapshots.ForPaths("a.docx", "b.pdf"), new EmptyParameters(), CancellationToken.None);
            Assert.Contains(preview.Warnings, w => w.Contains("nicht installierte", StringComparison.Ordinal));
        }
        finally { CultureInfo.CurrentUICulture = old; }
    }

    private static byte[] Page(int width)
    {
        using var output = new MemoryStream();
        var builder = new PdfDocumentBuilder(); builder.AddBlankPage(width, 700); builder.Save(output); return output.ToArray();
    }
    private sealed class Verifier : IFileTypeVerifier
    {
        public ValueTask<FileTypeVerification> VerifyAsync(string path, FileInputFilter filter, CancellationToken ct) =>
            new(FileTypeVerification.Accepted(Path.GetExtension(path) switch { ".pdf" => FileFormats.Pdf, ".jpg" => FileFormats.Jpeg, _ => OfficeFormats.Word }));
    }
    private sealed class Converter : IOfficeConverter
    {
        public List<string> Targets { get; } = new();
        public bool Fail { get; init; }
        public CancellationTokenSource? Cancel { get; init; }
        public bool IsAvailable(OfficeApplication app) => true;
        public async Task ConvertToPdfAsync(string source, string target, CancellationToken ct)
        {
            Targets.Add(target); await File.WriteAllBytesAsync(target, Page(400), ct);
            if (Fail) throw new InvalidOperationException("private-data");
            Cancel?.Cancel(); ct.ThrowIfCancellationRequested();
        }
        public Task ConvertPdfToWordAsync(string source, string target, CancellationToken ct) => throw new NotSupportedException();
    }
}
