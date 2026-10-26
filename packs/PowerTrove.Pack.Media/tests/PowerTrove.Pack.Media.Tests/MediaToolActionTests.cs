namespace PowerTrove.Pack.Media.Tests;

public sealed class MediaToolActionTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    public void Dispose() => _dir.Dispose();
    private static FileActionServices Services() => FileActionServices.CreatePhysical(new ExtensionOnlyTypeVerifier());
    [Fact]
    public async Task TrimPassesOptionsAndRejectsInvalidAndOutOfDurationRanges()
    {
        string source = _dir.Write("private.wav", new byte[100]); var engine = new Engine(); var action = new MediaToolAction("trim", new([engine]), new Inspector(), Services()); var snapshot = Snapshots.ForPaths(source);
        Assert.True((await action.PreviewAsync(snapshot, Snapshots.Parameters(("Start", "2"), ("End", "1")), default)).IsBlocked);
        var failure = await action.ExecuteAsync(snapshot, Snapshots.Parameters(("Start", "0"), ("End", "61")), null, default);
        Assert.False(failure.IsSuccess); Assert.Null(engine.LastRequest); Assert.DoesNotContain("private", failure.Failures[0].ErrorMessage);
        var result = await action.ExecuteAsync(snapshot, Snapshots.Parameters(("Start", "0.2"), ("End", "0.7"), ("Precise", "true")), null, default);
        Assert.True(result.IsSuccess); Assert.Equal(MediaOperation.Trim, engine.LastRequest!.Operation); Assert.Equal("0.2", engine.LastRequest.Options!["Start"]); Assert.Equal("True", engine.LastRequest.Options["Precise"]);
        Assert.EndsWith("(trimmed).wav", Assert.Single(result.OutputPaths)); Assert.Equal(100, new FileInfo(source).Length);
    }
    [Fact]
    public async Task CompressionSkipsLargerOutputAndPublishesSmallerCopy()
    {
        string source = _dir.Write("source.mp4", new byte[100]); var engine = new Engine { OutputBytes = 101 }; var action = new MediaToolAction("compress", new([engine]), new Inspector(), Services());
        var result = await action.ExecuteAsync(Snapshots.ForPaths(source), new EmptyParameters(), null, default);
        Assert.True(result.IsSuccess); Assert.Empty(result.OutputPaths); Assert.Empty(_dir.NewFiles(source));
        engine.OutputBytes = 20; result = await action.ExecuteAsync(Snapshots.ForPaths(source), Snapshots.Parameters(("Quality", "small"), ("KeepAudio", "false")), null, default);
        Assert.True(result.IsSuccess); Assert.Single(result.OutputPaths); Assert.Equal("small", engine.LastRequest!.Options!["Quality"]); Assert.Equal("False", engine.LastRequest.Options["KeepAudio"]);
    }
    [Fact]
    public async Task GifLimitBlocksPreviewAndIntervalWarns()
    {
        string source = _dir.Write("source.mp4", new byte[100]); var engine = new Engine(); var inspector = new Inspector();
        var gif = new MediaToolAction("to-gif", new([engine]), inspector, Services());
        var preview = await gif.PreviewAsync(Snapshots.ForPaths(source), Snapshots.Parameters(("Duration", "30"), ("Fps", "30")), default);
        Assert.True(preview.IsBlocked); Assert.Null(engine.LastRequest);
        var frames = new MediaToolAction("extract-frames", new([engine]), inspector, Services());
        Assert.Contains((await frames.PreviewAsync(Snapshots.ForPaths(source), Snapshots.Parameters(("Mode", "interval")), default)).Warnings, w => w.Contains("500"));
    }
    [Fact]
    public async Task MergeRejectsMixedFamiliesUsesNaturalOrderAndWarnsAboutSizes()
    {
        string video10 = _dir.Write("video10.mp4", "x"), video2 = _dir.Write("video2.mp4", "x"), audio = _dir.Write("audio.wav", "x");
        var engine = new Engine(); var inspector = new Inspector { VariableSize = true }; var action = new JoinMediaAction(new([engine]), inspector, Services());
        var mixed = await action.ExecuteAsync(Snapshots.ForPaths(video2, audio), new EmptyParameters(), null, default); Assert.False(mixed.IsSuccess); Assert.Null(engine.LastRequest);
        var preview = await action.PreviewAsync(Snapshots.ForPaths(video10, video2), new EmptyParameters(), default); Assert.NotEmpty(preview.Warnings);
        var result = await action.ExecuteAsync(Snapshots.ForPaths(video10, video2), new EmptyParameters(), null, default);
        Assert.True(result.IsSuccess); Assert.Equal(video2, engine.LastSource); Assert.Equal(video10, Assert.Single(engine.LastRequest!.AdditionalInputs!));
    }
    [Fact]
    public async Task NativeRejectionCleansTemporaryOutputBeforeFallback()
    {
        string source = _dir.Write("a.wav", "input"); var first = new Engine { Reject = true }; var second = new Engine();
        var action = new MediaToolAction("trim", new([first, second]), new Inspector(), Services());
        var result = await action.ExecuteAsync(Snapshots.ForPaths(source), new EmptyParameters(), null, default);
        Assert.True(result.IsSuccess); Assert.NotNull(second.LastRequest); Assert.Single(result.OutputPaths);
    }
    [Theory]
    [InlineData("normalize", "a.wav")][InlineData("rotate", "a.mp4")]
    public async Task OptionalFfmpegActionsRemainDiscoverableWithReason(string kind, string name)
    {
        var action = new MediaToolAction(kind, new([]), new Inspector(), Services());
        var match = await action.MatchAsync(Snapshots.ForPaths(_dir.Write(name, "x")), default);
        Assert.False(match.IsAvailable); Assert.Contains("FFmpeg", match.Reason);
    }
    [Fact]
    public async Task InformationIsReadOnlyAndHasNoInputNameInReceipt()
    {
        string source = _dir.Write("private.mp4", "x"); var result = await new MediaInfoAction(new Inspector(), Services()).ExecuteAsync(Snapshots.ForPaths(source), new EmptyParameters(), null, default);
        Assert.True(result.IsSuccess); Assert.Empty(result.OutputPaths); Assert.Contains("h264", result.Message); Assert.DoesNotContain("private", result.Message);
    }
    private sealed class Inspector : IMediaInspector
    {
        public bool IsAvailable => true; public bool VariableSize { get; init; }
        public Task<MediaDetails> InspectAsync(string path, CancellationToken ct) => Task.FromResult(new MediaDetails(TimeSpan.FromSeconds(60), path.EndsWith("wav") ? null : VariableSize && path.Contains("10") ? 1280 : 640, path.EndsWith("wav") ? null : 360, 30, "h264", "aac", 1000000, 2, 48000, true));
    }
    private sealed class Engine : IMediaEngine
    {
        public string Name => "test"; public bool IsAvailable => true; public bool Supports(MediaRequest r) => true;
        public int OutputBytes { get; set; } = 10; public bool Reject { get; init; } public MediaRequest? LastRequest { get; private set; } public string? LastSource { get; private set; }
        public Task ProcessAsync(string source, string dest, MediaRequest r, CancellationToken ct)
        {
            LastRequest = r; LastSource = source;
            Assert.False(File.Exists(dest)); File.WriteAllBytes(dest, new byte[OutputBytes]);
            if (Reject) throw new MediaEngineUnsupportedException(); return Task.CompletedTask;
        }
    }
}
