namespace PowerTrove.Pack.Media.Tests;

public sealed class SubtitleActionTests : IDisposable
{
    private readonly TempDirectory _directory = new();
    public void Dispose() => _directory.Dispose();
    private static IAction Action(string kind)
    {
        var services = FileActionServices.CreatePhysical(new ExtensionOnlyTypeVerifier());
        return new MediaPack(new CompositeMediaEngine([]), services).Actions.Single(a => a.Descriptor.Id == "powertrove.subtitle." + kind);
    }
    [Fact]
    public async Task SubtitleActionsWorkWithoutMediaEngineAndPreserveSourcesAcrossBatch()
    {
        const string text = "1\n00:00:02,000 --> 00:00:03,000\nGrüße\n";
        string first = _directory.Write("a.srt", text), second = _directory.Write("b.srt", text);
        var action = Action("srt-to-vtt"); var context = Snapshots.ForPaths(first, second);
        Assert.True((await action.MatchAsync(context, default)).IsAvailable);
        var preview = await action.PreviewAsync(context, new EmptyParameters(), default);
        Assert.False(preview.IsBlocked); Assert.DoesNotContain("Grüße", preview.PreviewText);
        var result = await action.ExecuteAsync(context, new EmptyParameters(), null, default);
        Assert.True(result.IsSuccess, result.Message); Assert.Equal(2, result.OutputPaths.Count);
        Assert.All(result.OutputPaths, path => Assert.Contains("00:00:02.000", File.ReadAllText(path)));
        string vtt = result.OutputPaths[0]; var shift = Action("shift");
        var shifted = await shift.ExecuteAsync(Snapshots.ForPaths(vtt), Snapshots.Parameters(("OffsetMs", "-1000")), null, default);
        Assert.True(shifted.IsSuccess, shifted.Message); Assert.Contains("00:00:01.000", File.ReadAllText(Assert.Single(shifted.OutputPaths)));
        var back = await Action("vtt-to-srt").ExecuteAsync(Snapshots.ForPaths(vtt), new EmptyParameters(), null, default);
        Assert.True(back.IsSuccess, back.Message); Assert.NotEqual(first, back.OutputPaths[0]); Assert.Equal(text, File.ReadAllText(first));
    }
    [Theory]
    [InlineData("1\n00:00:00,500 --> 00:00:01,000\nText\n", "-1000")]
    [InlineData("invalid", "0")]
    public async Task InvalidShiftOrInputBlocksPreviewWithoutPublishingOutput(string content, string offset)
    {
        string path = _directory.Write("input.srt", content); var action = Action("shift"); var options = Snapshots.Parameters(("OffsetMs", offset));
        Assert.True((await action.PreviewAsync(Snapshots.ForPaths(path), options, default)).IsBlocked);
        var receipt = await action.ExecuteAsync(Snapshots.ForPaths(path), options, null, default);
        Assert.False(receipt.IsSuccess); Assert.Empty(receipt.OutputPaths); Assert.Empty(_directory.NewFiles(path));
    }
}
