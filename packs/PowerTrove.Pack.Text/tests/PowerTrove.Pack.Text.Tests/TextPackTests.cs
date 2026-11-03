using PowerTrove.Pack.Text;
using PowerTrove.Abstractions.Manifest;

namespace PowerTrove.Pack.Text.Tests;

public sealed class TextPackTests
{
    private static ContextSnapshot Snapshot(string text, bool stale = false) => new(
        Guid.NewGuid(), new SourceWindowIdentity((nint)123, 123, "test", DateTimeOffset.UtcNow),
        ContextStatus.Available, new TextSelection(text), DateTimeOffset.UtcNow,
        stale ? DateTimeOffset.UtcNow.AddSeconds(-1) : DateTimeOffset.UtcNow.AddMinutes(1),
        "test", null);

    [Fact]
    public void TextSelectionDiagnosticNeverIncludesItsContent()
    {
        var selection = new TextSelection("SECRET-CONTENT");
        Assert.DoesNotContain("SECRET-CONTENT", selection.ToString());
        Assert.Contains("Length", selection.ToString());
    }

    [Fact]
    public void ManifestUsesExplicitTextPermissionsInsteadOfFilePermissions()
    {
        var manifest = ActionPackManifest.Create(new TextPack());
        Assert.All(manifest.Actions, action => Assert.Equal(new[] { "text" }, action.InputKinds));
        Assert.Contains("text.read:explicit", manifest.Permissions!);
        Assert.Contains("clipboard.write:approved", manifest.Permissions!);
        Assert.DoesNotContain("files.read:selected", manifest.Permissions!);
        _ = ActionPackManifest.Read(ActionPackManifest.ToJson(manifest));
    }

    [Fact]
    public void AllActionsHaveStableUniqueIds()
    {
        var pack = new TextPack();
        Assert.Equal("powertrove.text", pack.Info.Id);
        Assert.Equal(TextPack.Modes.Count, pack.Actions.Count);
        Assert.Equal(pack.Actions.Count, pack.Actions.Select(a => a.Descriptor.Id).Distinct().Count());
        Assert.All(pack.Actions, a =>
        {
            Assert.Contains(ContentKind.Text, a.Descriptor.Accepts);
            Assert.False(a.Descriptor.CanRememberParameters);
        });
    }

    [Theory]
    [InlineData("trim", "  hi  ", "hi")]
    [InlineData("trim-lines", " a  \r\n b ", "a\nb")]
    [InlineData("collapse-whitespace", " a  b\n c ", "a b c")]
    [InlineData("remove-empty-lines", "one\n \n two", "one\n two")]
    [InlineData("unique-lines", "a\nb\na", "a\nb")]
    [InlineData("sort-lines", "z\na", "a\nz")]
    [InlineData("lowercase", "ABC", "abc")]
    [InlineData("uppercase", "abc", "ABC")]
    [InlineData("url-encode", "a b", "a%20b")]
    [InlineData("url-decode", "a%20b", "a b")]
    [InlineData("html-encode", "<tag>", "&lt;tag&gt;")]
    [InlineData("html-decode", "&lt;tag&gt;", "<tag>")]
    [InlineData("base64-encode", "hi", "aGk=")]
    [InlineData("base64-decode", "aGk=", "hi")]
    [InlineData("json-escape", "hi", "\"hi\"")]
    public void CommonTransforms(string mode, string source, string expected) =>
        Assert.Equal(expected, TextTransforms.Apply(mode, source));

    [Fact]
    public async Task ExecuteReturnsClipboardTextWithoutChangingFiles()
    {
        var action = new TextTransformAction("trim");
        var input = Snapshot(" hi ");
        Assert.True((await action.MatchAsync(input, default)).IsAvailable);
        var preview = await action.PreviewAsync(input, new EmptyParameters(), default);
        Assert.False(preview.IsBlocked);
        Assert.DoesNotContain(" hi ", preview.PreviewText ?? "");
        var receipt = await action.ExecuteAsync(input, new EmptyParameters(), null, default);
        Assert.True(receipt.IsSuccess);
        Assert.Equal("hi", receipt.ClipboardText);
        Assert.Empty(receipt.OutputPaths);
    }

    [Fact]
    public async Task RegexReplacementUsesExplicitParameters()
    {
        var action = new TextTransformAction("regex-replace");
        var parameters = new DictionaryParameters(new Dictionary<string, string>
        {
            ["pattern"] = @"\d+", ["replacement"] = "#"
        });
        var receipt = await action.ExecuteAsync(Snapshot("a123b"), parameters, null, default);
        Assert.True(receipt.IsSuccess);
        Assert.Equal("a#b", receipt.ClipboardText);
    }

    [Fact]
    public async Task InvalidRegexFailsClosed()
    {
        var action = new TextTransformAction("regex-replace");
        var parameters = new DictionaryParameters(new Dictionary<string, string> { ["pattern"] = "[" });
        Assert.True((await action.PreviewAsync(Snapshot("hello"), parameters, default)).IsBlocked);
        Assert.False((await action.ExecuteAsync(Snapshot("hello"), parameters, null, default)).IsSuccess);
    }

    [Fact]
    public async Task StaleContentCannotExecute()
    {
        var action = new TextTransformAction("trim");
        Assert.False((await action.MatchAsync(Snapshot("data", true), default)).IsAvailable);
        Assert.False((await action.ExecuteAsync(Snapshot("data", true), new EmptyParameters(), null, default)).IsSuccess);
    }

    [Fact]
    public void InvalidBase64CannotProduceClipboardText() =>
        Assert.Throws<FormatException>(() => TextTransforms.Apply("base64-decode", "!invalid!"));

    [Fact]
    public void RegexExpansionIsBoundedDuringReplacement()
    {
        Assert.Throws<ArgumentException>(() => TextTransforms.Apply("regex-replace",
            new string('a', 500), "a", new string('b', 200)));
    }

    [Fact]
    public void OversizedInputIsRejected() =>
        Assert.Throws<ArgumentException>(() => TextTransforms.Apply("trim", new string('x', TextTransforms.MaximumCharacters + 1)));
}
