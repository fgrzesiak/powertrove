using System.Text.Json;

namespace FileKit.Tabular.Tests;

public sealed class JsonObjectTableTests
{
    [Fact]
    public void ColumnDiscoveryKeepsOrderAndValidatesAmbiguityWithoutReturningValues()
    {
        using var input = System.Text.Json.JsonDocument.Parse("""[{"id":"private-value","nested":{"city":"private-city"}},{"other":true}]""");
        Assert.Equal(new[] { "id", "nested.city", "other" }, JsonObjectTable.DiscoverColumns(input.RootElement, true));
        using var collision = System.Text.Json.JsonDocument.Parse("""[{"a.b":1,"a":{"b":2}}]""");
        Assert.Throws<TabularException>(() => JsonObjectTable.DiscoverColumns(collision.RootElement, true));
        Assert.Throws<OperationCanceledException>(() => JsonObjectTable.DiscoverColumns(input.RootElement, true, new(true)));
    }
    private static Table Project(string text, bool flatten = false, string[]? columns = null)
    { using var doc = JsonDocument.Parse(text); return JsonObjectTable.Project(doc.RootElement, flatten, columns); }

    [Fact]
    public void UnionColumnsRetainTypesAndMissingValuesWithoutGuessingDates()
    {
        var table = Project("""[{"id":7,"active":true,"date":"2026-10-26"},{"later":"=1+1","id":null}]""");
        Assert.Equal(new[] { "id", "active", "date", "later" }, table.Headers);
        Assert.Equal(new NumberCell(7), table.Rows[0][0]); Assert.Equal(new BooleanCell(true), table.Rows[0][1]);
        Assert.Equal(new TextCell("2026-10-26"), table.Rows[0][2]); Assert.IsType<EmptyCell>(table.Rows[1][0]);
        Assert.Equal(new TextCell("=1+1"), table.Rows[1][3]);
    }
    [Fact]
    public void FlatteningIsOptionalAndSelectedColumnsKeepRequestedOrder()
    {
        const string data = """[{"id":1,"person":{"name":"Ada","active":false},"tags":[1,2]}]""";
        Assert.Equal(new TextCell("{\"name\":\"Ada\",\"active\":false}"), Project(data).Rows[0][1]);
        var flat = Project(data, true, ["tags", "person.active", "id"]);
        Assert.Equal(new[] { "tags", "person.active", "id" }, flat.Headers);
        Assert.Equal(new TextCell("[1,2]"), flat.Rows[0][0]); Assert.Equal(new BooleanCell(false), flat.Rows[0][1]);
    }
    [Theory]
    [InlineData("9007199254740993")][InlineData("1e100")][InlineData("1e-100")][InlineData("1e-1000")][InlineData("0.99999999999999999999999999999999")][InlineData("1.12345678901234567")]
    public void NumbersOutsideExcelPrecisionRemainVerbatimText(string number)
    { Assert.Equal(new TextCell(number), Project("[{\"n\":" + number + "}]").Rows[0][0]); }
    [Theory]
    [InlineData("[]")][InlineData("[{}]")][InlineData("{}")] [InlineData("[1]")]
    [InlineData("[{\"a\":1,\"a\":2}]")]
    [InlineData("[{\"a.b\":1},{\"a\":{\"b\":2}}]")]
    public void InvalidShapesAndAmbiguousFlattenedNamesAreRejected(string data)
    { Assert.Throws<TabularException>(() => Project(data, true)); }
    [Fact]
    public void UnknownOrDuplicateColumnsAndCancellationAreRejected()
    {
        Assert.Throws<TabularException>(() => Project("[{\"a\":1}]", columns: ["missing"]));
        Assert.Throws<TabularException>(() => Project("[{\"a\":1}]", columns: ["a", "a"]));
        using var doc = JsonDocument.Parse("[{\"a\":1}]");
        Assert.Throws<OperationCanceledException>(() => JsonObjectTable.Project(doc.RootElement, cancellationToken: new(true)));
    }
}
