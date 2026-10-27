using PowerTrove.Abstractions.Packs;
using X = FileKit.Xlsx;

namespace PowerTrove.Pack.Data.Tests;

public sealed class JsonToExcelActionTests : IDisposable
{
    private readonly TempDirectory _directory = new();
    public void Dispose() => _directory.Dispose();
    private static IAction Action()
    {
        var host = new PackHostServices(new Version(0, 1, 0), FileActionServices.CreatePhysical(new ExtensionOnlyTypeVerifier()));
        return new DataPackFactory().Create(host).Actions.Single(a => a.Descriptor.Id == "powertrove.data.json-to-xlsx");
    }
    [Fact]
    public async Task PreviewSuppliesOnlyOrderedFieldNamesAndRefreshesThemWhenFlatteningChanges()
    {
        string source = _directory.Write("fields.json", """[{"id":"private-value","customer":{"name":"private-name"},"active":true}]""");
        var action = Action(); var context = Snapshots.ForPaths(source);
        Assert.Equal(ActionParameterEditor.MultiSelect, action.Descriptor.Parameters!.Single(p => p.Name == "Columns").Editor);
        var ordinary = await action.PreviewAsync(context, new EmptyParameters(), default);
        var nested = Assert.Single(ordinary.ParameterChoices!);
        Assert.Equal(new[] { "id", "customer", "active" }, nested.Choices.Select(c => c.Value));
        var flat = await action.PreviewAsync(context, Snapshots.Parameters(("Flatten", "true")), default);
        var fields = Assert.Single(flat.ParameterChoices!);
        Assert.NotEqual(nested.ContextKey, fields.ContextKey);
        Assert.Equal(new[] { "id", "customer.name", "active" }, fields.Choices.Select(c => c.Value));
        Assert.DoesNotContain("private", System.Text.Json.JsonSerializer.Serialize(fields));
        Assert.DoesNotContain("customer", flat.PreviewText);
    }

    [Fact]
    public async Task EmptyOrStaleSelectionsRetainChoicesSoTheUserCanRepairThem()
    {
        string source = _directory.Write("repair.json", """[{"id":1,"name":"demo"}]""");
        foreach (string selection in new[] { "[]", "[\"missing\"]", "invalid" })
        {
            var preview = await Action().PreviewAsync(Snapshots.ForPaths(source), Snapshots.Parameters(("Columns", selection)), default);
            Assert.True(preview.IsBlocked);
            Assert.Equal(new[] { "id", "name" }, Assert.Single(preview.ParameterChoices!).Choices.Select(c => c.Value));
        }
    }

    [Fact]
    public async Task MixedBatchOffersCommonFieldsWhileAllFieldsKeepsEachFilesStructure()
    {
        string a = _directory.Write("batch-a.json", """[{"id":1,"first":true}]"""), b = _directory.Write("batch-b.json", """[{"id":2,"second":true}]""");
        var action = Action(); var context = Snapshots.ForPaths(a, b);
        var all = await action.PreviewAsync(context, new EmptyParameters(), default);
        Assert.False(all.IsBlocked); Assert.Equal("id", Assert.Single(Assert.Single(all.ParameterChoices!).Choices).Value);
        var receipt = await action.ExecuteAsync(context, new EmptyParameters(), null, default);
        Assert.True(receipt.IsSuccess);
        foreach (string output in receipt.OutputPaths)
        { using var input = File.OpenRead(output); Assert.Equal(2, (await X.XlsxReader.ReadAsync(input)).Sheets[0].Rows[0].Count); }
        var one = await action.PreviewAsync(context, Snapshots.Parameters(("Columns", "[\"id\"]")), default);
        Assert.False(one.IsBlocked);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => action.PreviewAsync(context, new EmptyParameters(), new(true)).AsTask());
    }
    [Fact]
    public async Task RegisteredActionPreviewsCountsAndWritesTypedCollisionSafeWorkbooksForBatch()
    {
        const string json = """[{"id":42,"account":{"active":true},"precise":9007199254740993,"text":"=SUM(A1)"}]""";
        string first = _directory.Write("a.json", json), second = _directory.Write("b.json", json);
        string existing = _directory.Write("a.xlsx", "existing");
        var action = Action(); var context = Snapshots.ForPaths(first, second);
        var options = Snapshots.Parameters(("Flatten", "true"), ("Columns", "[\"account.active\",\"id\",\"precise\",\"text\"]"));
        Assert.True((await action.MatchAsync(context, default)).IsAvailable);
        var preview = await action.PreviewAsync(context, options, default);
        Assert.False(preview.IsBlocked); Assert.Equal(2, preview.ExpectedOutputs.Count);
        Assert.DoesNotContain("account", preview.PreviewText); Assert.DoesNotContain("SUM", preview.PreviewText);
        var receipt = await action.ExecuteAsync(context, options, null, default);
        Assert.True(receipt.IsSuccess, receipt.Message); Assert.Equal(2, receipt.OutputPaths.Count);
        foreach (string path in receipt.OutputPaths)
        {
            using var stream = File.OpenRead(path); var book = await X.XlsxReader.ReadAsync(stream);
            var sheet = Assert.Single(book.Sheets); Assert.Equal(2, sheet.Rows.Count);
            Assert.Equal(new X.BooleanCell(true), sheet.Rows[1][0]); Assert.Equal(new X.NumberCell(42), sheet.Rows[1][1]);
            Assert.Equal(new X.TextCell("9007199254740993"), sheet.Rows[1][2]); Assert.Equal(new X.TextCell("=SUM(A1)"), sheet.Rows[1][3]);
            Assert.False(book.HasFormulas);
        }
        Assert.Equal("existing", File.ReadAllText(existing)); Assert.Equal(json, File.ReadAllText(first));
    }
    [Theory]
    [InlineData("[{\"a\":1}]", "[\"missing\"]")][InlineData("[]", "")][InlineData("[null]", "")]
    [InlineData("{broken", "")][InlineData("[{\"a\":1}]", "a")]
    public async Task InvalidInputOrColumnsBlockPreviewAndPublishNothing(string json, string columns)
    {
        string source = _directory.Write("input.json", json); var action = Action(); var parameters = Snapshots.Parameters(("Columns", columns));
        Assert.True((await action.PreviewAsync(Snapshots.ForPaths(source), parameters, default)).IsBlocked);
        var receipt = await action.ExecuteAsync(Snapshots.ForPaths(source), parameters, null, default);
        Assert.False(receipt.IsSuccess); Assert.Empty(receipt.OutputPaths); Assert.Empty(_directory.NewFiles(source));
    }
}
