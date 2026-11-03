using System.Text.Json;

namespace PowerTrove.Abstractions.Tests;

public sealed class FileOrderParameterTests
{
    private static SelectedFile File(string path) =>
        new(path, Path.GetFileName(path), Path.GetExtension(path), null, null, true, true, null);

    private static readonly SelectedFile[] Files = [File(@"C:\docs\a.md"), File(@"C:\docs\b.md"), File(@"C:\other\a.md")];

    [Fact]
    public void ChoicesShowNamesButValuesRevealNothingAboutThePaths()
    {
        var set = FileOrderParameter.Choices("Order", Files, "help");
        Assert.Equal("Order", set.ParameterName);
        Assert.Equal(["a.md", "b.md", "a.md"], set.Choices.Select(c => c.Title));
        Assert.Equal(3, set.Choices.Select(c => c.Value).Distinct().Count());
        Assert.All(set.Choices, c => Assert.Matches("^[0-9a-f]{12}$", c.Value));
        Assert.Equal(set.ContextKey, FileOrderParameter.Choices("Order", Files).ContextKey);
        Assert.NotEqual(set.ContextKey, FileOrderParameter.Choices("Order", Files.Reverse().ToArray()).ContextKey);
    }

    [Fact]
    public void TheChosenOrderAndSubsetApplyAndForeignValuesFallBackToAllFiles()
    {
        var keys = FileOrderParameter.Choices("Order", Files).Choices.Select(c => c.Value).ToArray();
        Assert.Equal(Files, FileOrderParameter.Apply(Files, f => f, ""));
        Assert.Equal([Files[2], Files[0]], FileOrderParameter.Apply(Files, f => f, JsonSerializer.Serialize(new[] { keys[2], "000000000000", keys[0] })));
        Assert.Equal(Files, FileOrderParameter.Apply(Files, f => f, "[\"000000000000\"]"));
        Assert.Equal(Files, FileOrderParameter.Apply(Files, f => f, "not json"));
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("[\"a\",\"b\"]", true)]
    [InlineData("[\"a\",\"a\"]", false)]
    [InlineData("[1]", false)]
    [InlineData("{}", false)]
    [InlineData("[\"\"]", false)]
    public void OnlyBlankOrDistinctKeysAreValid(string value, bool valid) => Assert.Equal(valid, FileOrderParameter.IsValid(value));

    [Fact]
    public void TheEditorHintRoundTripsThroughTheManifest()
    {
        var parameter = ActionParameterDefinition.FileOrder("Order", "Files");
        Assert.Equal(ActionParameterKind.Text, parameter.Kind);
        Assert.Equal("", parameter.DefaultValue);
        Assert.Equal("fileOrder", PowerTrove.Abstractions.Manifest.ActionPackManifest.EditorName(parameter.Editor));
    }
}
