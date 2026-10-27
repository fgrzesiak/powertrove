using System.Text;
using System.Text.Json;

namespace PowerTrove.Pack.Data.Tests;

public sealed class DataActionsTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    public void Dispose() => _dir.Dispose();
    private static FileActionServices Services() => FileActionServices.CreatePhysical(new ExtensionOnlyTypeVerifier());
    private async Task<ActionReceipt> Run(string id, string[] paths, params (string Key, string Value)[] options) => await new DataPack(Services()).Actions.Single(a => a.Descriptor.Id == "powertrove.data." + id).ExecuteAsync(Snapshots.ForPaths(paths), Snapshots.Parameters(options), null, CancellationToken.None);
    [Fact]
    public void PackRegistersEveryImplementedDataAction()
    {
        string[] suffixes = { "merge-text", "to-utf8", "line-endings", "csv-to-json", "csv-to-markdown", "csv-to-sql", "merge-csv", "json-format", "json-minify", "xlsx-to-csv", "xlsx-to-json", "csv-to-xlsx", "json-validate", "json-to-csv", "json-to-xlsx", "vcf-to-csv", "csv-to-vcf", "vcf-to-xlsx", "xlsx-to-vcf", "merge-vcf" };
        Assert.Equal(suffixes.Select(s => "powertrove.data." + s), new DataPack(Services()).Actions.Select(a => a.Descriptor.Id));
    }
    [Theory]
    [InlineData("utf8", false)] [InlineData("utf8", true)] [InlineData("utf16le", true)] [InlineData("utf16be", true)] [InlineData("utf32le", true)] [InlineData("utf32be", true)] [InlineData("1252", false)]
    public async Task TextReaderDetectsEncodings(string name, bool bom)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding encoding = name switch { "utf8" => new UTF8Encoding(bom), "utf16le" => new UnicodeEncoding(false, true), "utf16be" => new UnicodeEncoding(true, true), "utf32le" => new UTF32Encoding(false, true), "utf32be" => new UTF32Encoding(true, true), _ => Encoding.GetEncoding(1252) };
        using var input = new MemoryStream(encoding.GetPreamble().Concat(encoding.GetBytes("Grüße")).ToArray());
        var text = await TextFileReader.ReadAsync(input); Assert.Equal("Grüße", text.Text); Assert.Equal(bom, text.HasBom); Assert.Equal(encoding.CodePage, text.Encoding.CodePage);
    }
    [Fact]
    public async Task TextReaderEnforcesLimitAndInvalidBom()
    {
        await Assert.ThrowsAsync<FileProcessingException>(() => TextFileReader.ReadAsync(new MemoryStream(new byte[10]), maxBytes: 9));
        await Assert.ThrowsAsync<FileProcessingException>(() => TextFileReader.ReadAsync(new MemoryStream(new byte[] { 0xFF, 0xFE, 0x00 })));
    }
    [Theory]
    [InlineData("none", "AB")][InlineData("blank-line", "A\n\nB")][InlineData("header", "===== a.txt =====\nA\n\n===== b.txt =====\nB")]
    public async Task MergeSeparatorsAreExactAndOrderIsNatural(string separator, string expected)
    {
        string a = _dir.Write("a.txt", "A"), b = _dir.Write("b.txt", "B");
        var result = await Run("merge-text", new[] { b, a }, ("Separator", separator));
        Assert.True(result.IsSuccess); Assert.Equal(expected, File.ReadAllText(Assert.Single(result.OutputPaths)));
    }
    [Fact]
    public async Task Utf8AndLineEndingsSkipWithoutProducingCopies()
    {
        string p = _dir.Write("a.txt", "a\nb\n");
        Assert.Empty((await Run("to-utf8", new[] { p })).OutputPaths);
        Assert.Empty((await Run("line-endings", new[] { p }, ("Style", "unix"))).OutputPaths);
        var converted = await Run("line-endings", new[] { p }, ("Style", "windows"));
        Assert.Equal("a\r\nb\r\n", File.ReadAllText(Assert.Single(converted.OutputPaths))); Assert.Equal("a\nb\n", File.ReadAllText(p));
    }
    [Fact]
    public async Task CsvConversionsUseSharedSerializersAndSafePreview()
    {
        string p = _dir.Write("data.csv", "Name,Note\r\nAda,secret-value\r\n");
        var action = new DataFileAction(Services(), "csv-to-json");
        var preview = await action.PreviewAsync(Snapshots.ForPaths(p), new EmptyParameters(), CancellationToken.None);
        Assert.Contains("1 rows, 2 columns", preview.PreviewText); Assert.DoesNotContain("secret-value", preview.PreviewText); Assert.DoesNotContain("Ada", preview.PreviewText);
        foreach (string id in new[] { "csv-to-json", "csv-to-markdown", "csv-to-sql" })
        {
            var receipt = await Run(id, new[] { p }); Assert.True(receipt.IsSuccess);
            var table = FileKit.Tabular.CsvReader.Parse(File.ReadAllText(p)); using var expected = new MemoryStream();
            if (id == "csv-to-json") FileKit.Tabular.JsonTableWriter.Write(expected, table);
            else { using var writer = new StreamWriter(expected, new UTF8Encoding(false), leaveOpen: true); if (id == "csv-to-markdown") FileKit.Tabular.MarkdownTableWriter.Write(writer, table); else FileKit.Tabular.SqlInsertWriter.Write(writer, table); }
            Assert.Equal(expected.ToArray(), File.ReadAllBytes(Assert.Single(receipt.OutputPaths)));
        }
    }
    [Fact]
    public async Task CsvMergeRequiresCompatibleHeadersOrFillsUnionColumns()
    {
        string a = _dir.Write("a.csv", "A,B\n1,2\n"), b = _dir.Write("b.csv", "B,C\n3,4\n");
        var failure = await Run("merge-csv", new[] { a, b }); Assert.False(failure.IsSuccess); Assert.Empty(failure.OutputPaths);
        var success = await Run("merge-csv", new[] { a, b }, ("Mode", "union")); Assert.True(success.IsSuccess);
        Assert.Equal("A,B,C\r\n1,2,\r\n,3,4\r\n", File.ReadAllText(Assert.Single(success.OutputPaths)));
    }
    [Theory]
    [InlineData("2")][InlineData("4")][InlineData("tabs")]
    public async Task JsonFormattingRetainsLargeNumbersAndValues(string indent)
    {
        const string text = "{\"n\":123456789012345678901234567890,\"s\":\"\\u00e4\",\"a\":[true,null]}";
        string p = _dir.Write("a.json", text); var formatted = await Run("json-format", new[] { p }, ("Indent", indent));
        string result = File.ReadAllText(Assert.Single(formatted.OutputPaths)); Assert.Contains("123456789012345678901234567890", result);
        using var parsed = JsonDocument.Parse(result); Assert.Equal("ä", parsed.RootElement.GetProperty("s").GetString());
        var minified = await Run("json-minify", new[] { formatted.OutputPaths[0] }); Assert.DoesNotContain('\n', File.ReadAllText(Assert.Single(minified.OutputPaths)));
    }
    [Fact]
    public async Task JsonValidationDoesNotLeakContentAndCsvHandlesMissingAndNestedValues()
    {
        string invalid = _dir.Write("secret.json", "{\nsecret-content}"); var result = await Run("json-validate", new[] { invalid });
        Assert.False(result.IsSuccess); Assert.Contains("line 2", Assert.Single(result.Failures).ErrorMessage); Assert.DoesNotContain("secret-content", result.Failures[0].ErrorMessage);
        string valid = _dir.Write("valid.json", "[{\"a\":1,\"nested\":{\"x\":2}},{\"b\":true}]");
        var csv = await Run("json-to-csv", new[] { valid });
        Assert.Equal("a,nested,b\r\n1,\"{\"\"x\"\":2}\",\r\n,,true\r\n", File.ReadAllText(Assert.Single(csv.OutputPaths)));
    }
    [Fact]
    public async Task CollisionPreservesPreviousOutputAndCanceledRunWritesNothing()
    {
        string p = _dir.Write("a.json", "{}"); string existing = _dir.Write("a (formatted).json", "keep");
        var receipt = await Run("json-format", new[] { p }); Assert.NotEqual(existing, Assert.Single(receipt.OutputPaths)); Assert.Equal("keep", File.ReadAllText(existing));
        var action = new DataFileAction(Services(), "json-minify"); var canceled = await action.ExecuteAsync(Snapshots.ForPaths(p), new EmptyParameters(), null, new CancellationToken(true)); Assert.True(canceled.IsCanceled); Assert.Empty(canceled.OutputPaths);
    }
}

public abstract class DataContract : ActionContractTestsBase
{
    protected virtual string Kind => "json-format";
    protected override IAction CreateAction() => new DataPack(FileActionServices.CreatePhysical(new ExtensionOnlyTypeVerifier())).Actions.Single(a => a.Descriptor.Id == "powertrove.data." + Kind);
    protected override ContextSnapshot CreateValidSnapshot() => Snapshots.ForPaths(Path.Combine(Path.GetTempPath(), "example.json"));
}
public sealed class JsonActionContract : DataContract { }
public sealed class TextActionContract : DataContract { protected override string Kind => "to-utf8"; }
public sealed class CsvActionContract : DataContract { protected override string Kind => "csv-to-json"; }
public sealed class CsvMergeContract : DataContract { protected override string Kind => "merge-csv"; }
public sealed class TextMergeContract : DataContract { protected override string Kind => "merge-text"; }
public sealed class JsonValidationContract : DataContract { protected override string Kind => "json-validate"; }
