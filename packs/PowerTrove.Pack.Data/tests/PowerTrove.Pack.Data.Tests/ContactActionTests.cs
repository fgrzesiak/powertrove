using T = FileKit.Tabular;
using X = FileKit.Xlsx;

namespace PowerTrove.Pack.Data.Tests;

public sealed class ContactActionTests : IDisposable
{
    private readonly TempDirectory _directory = new();
    public void Dispose() => _directory.Dispose();
    private static IAction Action(string kind) => new DataPack(FileActionServices.CreatePhysical(new ExtensionOnlyTypeVerifier()))
        .Actions.Single(a => a.Descriptor.Id == "powertrove.data." + kind);
    private const string Card = "BEGIN:VCARD\r\nVERSION:3.0\r\nFN:Zoë Example\r\nN:Example;Zoë;;;\r\nTEL;TYPE=CELL:+49 123\r\nTEL;TYPE=WORK:555\r\nEMAIL:a@example.test\r\nEMAIL:b@example.test\r\nEND:VCARD\r\n";
    [Theory]
    [InlineData("vcf-to-csv", "csv-to-vcf")][InlineData("vcf-to-xlsx", "xlsx-to-vcf")]
    public async Task RegisteredContactConvertersPreserveCommonFieldsAcrossBatchAndRoundTrip(string export, string import)
    {
        string first = _directory.Write("a.vcf", Card), second = _directory.Write("b.vcf", Card);
        var action = Action(export); var context = Snapshots.ForPaths(first, second);
        Assert.True((await action.MatchAsync(context, default)).IsAvailable);
        var preview = await action.PreviewAsync(context, new EmptyParameters(), default);
        Assert.False(preview.IsBlocked); Assert.DoesNotContain("Zoë", preview.PreviewText); Assert.DoesNotContain("example.test", preview.PreviewText);
        var result = await action.ExecuteAsync(context, new EmptyParameters(), null, default);
        Assert.True(result.IsSuccess, result.Message); Assert.Equal(2, result.OutputPaths.Count);
        var back = await Action(import).ExecuteAsync(Snapshots.ForPaths(result.OutputPaths[0]), new EmptyParameters(), null, default);
        Assert.True(back.IsSuccess, back.Message); Assert.NotEqual(first, Assert.Single(back.OutputPaths));
        var contact = Assert.Single(T.VCard.Parse(File.ReadAllText(back.OutputPaths[0])));
        Assert.Equal(2, contact.Properties.Count(p => p.Name == "TEL")); Assert.Equal(2, contact.Properties.Count(p => p.Name == "EMAIL"));
        Assert.Equal(Card, File.ReadAllText(first));
        if (export == "vcf-to-xlsx") { using var input = File.OpenRead(result.OutputPaths[0]); Assert.False((await X.XlsxReader.ReadAsync(input)).HasFormulas); }
    }
    [Fact]
    public async Task MergePreservesUnknownFieldsAndDuplicatesWithoutChangingSources()
    {
        string first = _directory.Write("a.vcf", Card.Replace("END:VCARD", "X-KEPT:value\r\nEND:VCARD", StringComparison.Ordinal)), second = _directory.Write("b.vcf", Card);
        var result = await Action("merge-vcf").ExecuteAsync(Snapshots.ForPaths(first, second), new EmptyParameters(), null, default);
        Assert.True(result.IsSuccess, result.Message); string output = File.ReadAllText(Assert.Single(result.OutputPaths));
        Assert.Equal(2, T.VCard.Parse(output).Count); Assert.Contains("X-KEPT:value", output);
        Assert.Equal(Card, File.ReadAllText(second));
    }
    [Fact]
    public async Task UnsupportedVersionOrUnmappedImportColumnsBlockPreviewWithoutPublishing()
    {
        string vcf = _directory.Write("old.vcf", Card.Replace("3.0", "2.1", StringComparison.Ordinal));
        string csv = _directory.Write("unknown.csv", "Name,Unknown\nName,keep\n");
        foreach (var (kind, path) in new[] { ("vcf-to-csv", vcf), ("csv-to-vcf", csv) })
        {
            var action = Action(kind); Assert.True((await action.PreviewAsync(Snapshots.ForPaths(path), new EmptyParameters(), default)).IsBlocked);
            var receipt = await action.ExecuteAsync(Snapshots.ForPaths(path), new EmptyParameters(), null, default);
            Assert.False(receipt.IsSuccess); Assert.Empty(receipt.OutputPaths);
        }
        Assert.Empty(_directory.NewFiles(vcf, csv));
    }
}
