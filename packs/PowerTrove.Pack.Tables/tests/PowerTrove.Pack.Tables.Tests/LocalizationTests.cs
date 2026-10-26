using System.Globalization;

namespace PowerTrove.Pack.Tables.Tests;

public class LocalizationTests
{
    [Fact]
    public void EveryTableAction_HasLocalizedMetadata()
    {
        var problems = LocalizedActionCatalog.FindProblems(() =>
        {
            var services = FileActionServices.CreatePhysical(new ExtensionOnlyTypeVerifier());
            return new IActionPack[] { new TablesPack(services.Planner, services.FileSystem, services) };
        });
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Theory]
    [InlineData("en-US", 0, "The table could not be exported.")]
    [InlineData("en-US", 1, "The table export could not be saved.")]
    [InlineData("en-US", 2, "Access to the export destination was denied.")]
    [InlineData("en-US", 3, "The table export options or destination are invalid.")]
    [InlineData("en-US", 4, "The table data is invalid or exceeds the supported limits.")]
    [InlineData("de-DE", 0, "Die Tabelle konnte nicht exportiert werden.")]
    [InlineData("de-DE", 1, "Der Tabellenexport konnte nicht gespeichert werden.")]
    [InlineData("de-DE", 2, "Der Zugriff auf das Exportziel wurde verweigert.")]
    [InlineData("de-DE", 3, "Die Optionen oder das Ziel des Tabellenexports sind ungültig.")]
    [InlineData("de-DE", 4, "Die Tabellendaten sind ungültig oder überschreiten die unterstützten Grenzen.")]
    public async Task EveryExport_MapsErrorsWithoutPathsOrSelectedContent(string language, int kind, string expected)
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
            var fs = new FailingFileSystem(kind);
            var pack = new TablesPack(new OutputPlanner(fs), fs);
            var context = Snapshots.ForPaths() with { Content = new TableData(new CellValue[][] { new CellValue[] { new TextCell("PRIVATE_CELL") } }, null, null) };
            foreach (var action in pack.Actions)
            {
                var parameters = new DictionaryParameters(new Dictionary<string, string> { ["OutputDirectory"] = fs.Root });
                var receipt = await action.ExecuteAsync(context, parameters, null, default);
                Assert.False(receipt.IsSuccess);
                Assert.Equal(expected, Assert.Single(receipt.Failures).ErrorMessage);
                Assert.DoesNotContain("PRIVATE", receipt.Failures[0].ErrorMessage);
                Assert.Empty(receipt.OutputPaths);
            }
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }

    [Fact]
    public async Task AvailabilityPreviewAndStaleErrors_AreGerman()
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("de-DE");
            var fs = new FailingFileSystem(0);
            var pack = new TablesPack(new OutputPlanner(fs), fs);
            var invalid = Snapshots.ForPaths() with { Content = new NoContent() };
            foreach (var action in pack.Actions)
            {
                Assert.Equal("Die Auswahl ist keine Tabelle.", (await action.MatchAsync(invalid, default)).Reason);
                Assert.Equal("Keine Tabelle.", (await action.PreviewAsync(invalid, Snapshots.Parameters(), default)).PreviewText);
                var receipt = await action.ExecuteAsync(invalid.Stale(), Snapshots.Parameters(), null, default);
                Assert.Equal("Die Auswahl ist abgelaufen. Wählen Sie erneut aus.", Assert.Single(receipt.Failures).ErrorMessage);
            }
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }

    private sealed class FailingFileSystem(int kind) : IOutputFileSystem
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "table-export-localization-fixture");
        public bool FileExists(string path) => false;
        public bool DirectoryExists(string path) => path == Root;
        public void CreateDirectory(string path) { }
        public void DeleteFile(string path) { }
        public Stream OpenRead(string path) => throw new NotSupportedException();
        public Stream CreateFile(string path) => throw Failure();
        private Exception Failure() => kind switch
        {
            0 => new FileProcessingException("PRIVATE tool error"), 1 => new IOException("PRIVATE file path"),
            2 => new UnauthorizedAccessException("PRIVATE access details"), 3 => new ArgumentException("PRIVATE selected content"),
            _ => new FileKit.Tabular.TabularException("PRIVATE invalid table data")
        };
        public long GetFileLength(string path) => 0;
        public void MoveFile(string source, string destination) => throw new NotSupportedException();
        public void MoveDirectory(string source, string destination) => throw new NotSupportedException();
        public void DeleteDirectory(string path) => throw new NotSupportedException();
    }
}
