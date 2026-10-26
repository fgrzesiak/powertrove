using System.Globalization;
using System.IO.Compression;
using System.Text;
using PowerTrove.Abstractions.Office;
using PowerTrove.Infrastructure.Files;
using PowerTrove.Pack.Office;

namespace PowerTrove.Pack.Office.Tests;

public sealed class OfficeActionTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    public void Dispose() => _dir.Dispose();
    private static IAction Action(string id, IOfficeConverter? converter) => new OfficePack(FileActionServices.CreatePhysical(new FileSignatureVerifier()), converter).Actions.Single(a => a.Descriptor.Id == "contextbar.office." + id);

    [Theory]
    [InlineData("word-to-pdf", ".rtf", OfficeApplication.Word)]
    [InlineData("excel-to-pdf", ".csv", OfficeApplication.Excel)]
    [InlineData("powerpoint-to-pdf", ".pptx", OfficeApplication.PowerPoint)]
    [InlineData("pdf-to-word", ".pdf", OfficeApplication.Word)]
    public async Task Conversion_UsesCorrectPortAndCollisionSafeName(string id, string extension, OfficeApplication app)
    {
        string source = _dir.Write("sample" + extension, Input(extension));
        byte[] before = File.ReadAllBytes(source);
        string outputExtension = id == "pdf-to-word" ? ".docx" : ".pdf";
        string existing = _dir.Write("sample" + outputExtension, "existing");
        var converter = new Converter { Available = app };
        var action = Action(id, converter);
        var preview = await action.PreviewAsync(Snapshots.ForPaths(source), new EmptyParameters(), CancellationToken.None);
        Assert.False(preview.IsBlocked);
        Assert.NotEmpty(preview.Warnings);
        var receipt = await action.ExecuteAsync(Snapshots.ForPaths(source), new EmptyParameters(), null, CancellationToken.None);
        Assert.True(receipt.IsSuccess, receipt.Message);
        string output = Assert.Single(receipt.OutputPaths);
        Assert.NotEqual(existing, output);
        Assert.Equal(outputExtension, Path.GetExtension(output));
        Assert.Equal(before, File.ReadAllBytes(source));
        Assert.Equal("existing", File.ReadAllText(existing));
        Assert.Equal(id == "pdf-to-word", converter.Reflow);
        Assert.Single(converter.Sources);
        Assert.DoesNotContain(_dir.NewFiles(source, existing), p => Path.GetFileName(p).StartsWith(".~cb", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("word-to-pdf", ".rtf")]
    [InlineData("excel-to-pdf", ".csv")]
    [InlineData("powerpoint-to-pdf", ".pptx")]
    [InlineData("pdf-to-word", ".pdf")]
    public async Task MissingApplication_IsUnavailableAndWritesNothing(string id, string extension)
    {
        string source = _dir.Write("input" + extension, Input(extension));
        var action = Action(id, null);
        Assert.False((await action.MatchAsync(Snapshots.ForPaths(source), CancellationToken.None)).IsAvailable);
        Assert.True((await action.PreviewAsync(Snapshots.ForPaths(source), new EmptyParameters(), CancellationToken.None)).IsBlocked);
        var receipt = await action.ExecuteAsync(Snapshots.ForPaths(source), new EmptyParameters(), null, CancellationToken.None);
        Assert.False(receipt.IsSuccess); Assert.Empty(_dir.NewFiles(source));
    }

    [Theory]
    [InlineData(OfficeFailure.PasswordProtected)]
    [InlineData(OfficeFailure.TimedOut)]
    [InlineData(OfficeFailure.UnsafeMacros)]
    public async Task ExpectedErrors_AreLocalizedAndStagedOutputIsRemoved(OfficeFailure failure)
    {
        string source = _dir.Write("private source.rtf", Input(".rtf"));
        var old = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("de-DE");
            var receipt = await Action("word-to-pdf", new Converter { Failure = failure }).ExecuteAsync(Snapshots.ForPaths(source), new EmptyParameters(), null, CancellationToken.None);
            string message = Assert.Single(receipt.Failures).ErrorMessage;
            Assert.DoesNotContain("private source", message);
            Assert.DoesNotContain(failure.ToString(), message);
            Assert.Empty(_dir.NewFiles(source));
        }
        finally { CultureInfo.CurrentUICulture = old; }
    }

    [Fact]
    public async Task UnexpectedEngineText_IsNotShown()
    {
        string source = _dir.Write("input.rtf", Input(".rtf"));
        var receipt = await Action("word-to-pdf", new Converter { Unexpected = true }).ExecuteAsync(Snapshots.ForPaths(source), new EmptyParameters(), null, CancellationToken.None);
        Assert.DoesNotContain("secret-path", Assert.Single(receipt.Failures).ErrorMessage);
        Assert.Empty(_dir.NewFiles(source));
    }

    [Fact]
    public async Task RenamedNonOfficeFile_NeverReachesConverter()
    {
        string source = _dir.Write("input.docx", "%PDF-1.7\n");
        var converter = new Converter();
        var receipt = await Action("word-to-pdf", converter).ExecuteAsync(Snapshots.ForPaths(source), new EmptyParameters(), null, CancellationToken.None);
        Assert.False(receipt.IsSuccess); Assert.Empty(converter.Sources); Assert.Empty(_dir.NewFiles(source));
    }

    [Fact]
    public void EveryNewField_IsTranslated()
    {
        var old = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US"); var en = new OfficePack(FileActionServices.CreatePhysical(new ExtensionOnlyTypeVerifier()));
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("de-DE"); var de = new OfficePack(FileActionServices.CreatePhysical(new ExtensionOnlyTypeVerifier()));
            foreach (var a in en.Actions)
            {
                var b = de.Actions.Single(x => x.Descriptor.Id == a.Descriptor.Id);
                Assert.NotEqual(a.Descriptor.Title, b.Descriptor.Title);
                Assert.NotEqual(a.Descriptor.Description, b.Descriptor.Description);
            }
        }
        finally { CultureInfo.CurrentUICulture = old; }
    }

    private static byte[] Input(string extension)
    {
        if (extension == ".rtf") return "{\\rtf1\\ansi Example}"u8.ToArray();
        if (extension == ".csv") return "Name,Count\nExample,3\n"u8.ToArray();
        if (extension == ".pdf") return "%PDF-1.7\n"u8.ToArray();
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
        {
            using (var w = new StreamWriter(zip.CreateEntry("[Content_Types].xml").Open())) w.Write("<Types />");
            using (var w = new StreamWriter(zip.CreateEntry("ppt/presentation.xml").Open())) w.Write("<presentation />");
        }
        return memory.ToArray();
    }
    private sealed class Converter : IOfficeConverter
    {
        public OfficeApplication Available { get; init; } = OfficeApplication.Word;
        public OfficeFailure? Failure { get; init; }
        public bool Unexpected { get; init; }
        public bool Reflow { get; private set; }
        public List<string> Sources { get; } = new();
        public bool IsAvailable(OfficeApplication app) => app == Available;
        public Task ConvertToPdfAsync(string source, string target, CancellationToken ct) => Run(source, target, false, ct);
        public Task ConvertPdfToWordAsync(string source, string target, CancellationToken ct) => Run(source, target, true, ct);
        private async Task Run(string source, string target, bool reflow, CancellationToken ct)
        {
            Sources.Add(source); Reflow = reflow;
            await File.WriteAllTextAsync(target, "partial fake output", ct);
            if (Unexpected) throw new InvalidOperationException("secret-path");
            if (Failure.HasValue) throw new OfficeConversionException(Failure.Value);
        }
    }
}

public sealed class OfficeExportContract : ActionContractTestsBase
{
    protected override IAction CreateAction() => new OfficePack(FileActionServices.CreatePhysical(new ExtensionOnlyTypeVerifier())).Actions[0];
    protected override ContextSnapshot CreateValidSnapshot() => Snapshots.ForPaths("example.rtf");
}
public sealed class OfficeReflowContract : ActionContractTestsBase
{
    protected override IAction CreateAction() => new OfficePack(FileActionServices.CreatePhysical(new ExtensionOnlyTypeVerifier())).Actions[3];
    protected override ContextSnapshot CreateValidSnapshot() => Snapshots.ForPaths("example.pdf");
}
