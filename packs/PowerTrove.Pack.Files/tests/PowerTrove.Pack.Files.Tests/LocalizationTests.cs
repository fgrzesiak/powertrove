using System.Globalization;
using FileKit.Archive;

namespace PowerTrove.Pack.Files.Tests;

public class LocalizationTests
{
    private static FileActionServices Services() => FileActionServices.CreatePhysical(new ExtensionOnlyTypeVerifier());

    [Fact]
    public void EveryFileAction_HasLocalizedMetadata()
    {
        var problems = LocalizedActionCatalog.FindProblems(() => new IActionPack[] { new FilesPack(Services()) });
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Theory]
    [InlineData(0, "The archive exceeds the supported safety limits.", "Das Archiv überschreitet die unterstützten Sicherheitsgrenzen.")]
    [InlineData(1, "The archive contains an unsafe entry and cannot be extracted.", "Das Archiv enthält einen unsicheren Eintrag und kann nicht entpackt werden.")]
    [InlineData(2, "The archive is damaged, encrypted or unsupported.", "Das Archiv ist beschädigt, verschlüsselt oder wird nicht unterstützt.")]
    [InlineData(3, "The archive is invalid, unsafe or exceeds its limits.", "Das Archiv ist ungültig, unsicher oder überschreitet die Grenzen.")]
    public void ArchiveTypes_MapWithoutRawText(int kind, string english, string german)
    {
        const string raw = "PRIVATE /selected/file.zip and entry contents";
        ArchiveException exception = kind switch
        {
            0 => new ArchiveLimitExceededException(raw), 1 => new UnsafeArchiveEntryException(raw),
            2 => new InvalidArchiveException(raw), _ => new ArchiveException(raw)
        };
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            foreach (var (culture, expected) in new[] { ("en-US", english), ("de-DE", german) })
            {
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
                var mapped = ArchiveErrors.Wrap(exception);
                Assert.Equal(expected, mapped.Message);
                Assert.Same(exception, mapped.InnerException);
                Assert.DoesNotContain("PRIVATE", mapped.Message);
            }
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }

    [Fact]
    public async Task InvalidArchiveReceipt_IsLocalizedAndContainsNoInputName()
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("de-DE");
            using var directory = new TempDirectory();
            string path = directory.Write("PRIVATE.zip", "not an archive");
            var receipt = await new ExtractZipAction(Services()).ExecuteAsync(Snapshots.ForPaths(path), Snapshots.Parameters(), null, default);
            Assert.False(receipt.IsSuccess);
            Assert.Contains("Archiv", Assert.Single(receipt.Failures).ErrorMessage);
            Assert.DoesNotContain("PRIVATE", receipt.Failures[0].ErrorMessage);
            Assert.Empty(receipt.OutputPaths);
            Assert.Empty(directory.NewFiles(path));
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }

    [Fact]
    public async Task GeneratedSortNames_StayEnglishUnderGermanUi()
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("de-DE");
            using var directory = new TempDirectory();
            var receipt = await new SortByTypeAction(Services()).ExecuteAsync(Snapshots.ForPaths(
                directory.Write("a.png", "image"), directory.Write("b.pdf", "pdf")), Snapshots.Parameters(), null, default);
            Assert.True(receipt.IsSuccess);
            string output = Assert.Single(receipt.OutputPaths);
            Assert.Equal("Sorted", Path.GetFileName(output));
            Assert.True(Directory.Exists(Path.Combine(output, "Images")));
            Assert.True(Directory.Exists(Path.Combine(output, "PDFs")));
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }

    [Theory]
    [InlineData(1, "1 Pfad kopiert.")]
    [InlineData(2, "2 Pfade kopiert.")]
    public void CountMessages_UseSingularAndPlural(int count, string expected)
    {
        var previous = CultureInfo.CurrentUICulture;
        try { CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("de-DE"); Assert.Equal(expected, SR.Count("CopiedPaths", count)); }
        finally { CultureInfo.CurrentUICulture = previous; }
    }
}
