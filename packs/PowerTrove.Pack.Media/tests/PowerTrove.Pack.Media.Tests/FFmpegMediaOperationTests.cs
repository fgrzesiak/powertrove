namespace PowerTrove.Pack.Media.Tests;

public sealed class FFmpegMediaOperationTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    public void Dispose() => _dir.Dispose();
    private static MediaDetails Info(bool video = true, string? signature = null) => new(TimeSpan.FromSeconds(2), video ? 640 : null, video ? 360 : null, 30, video ? "h264" : null, "aac", 1000000, 2, 48000, true, signature);
    private static MediaRequest Request(MediaOperation op, string target, string extension, params (string Key, string Value)[] values) => new(op, target, extension, Options: values.ToDictionary(v => v.Key, v => v.Value), InputDetails: [Info()]);
    [Fact]
    public async Task CompressionUsesTheDurationBudgetForVideoRateAndRetainsAudioReservation()
    {
        var fake = new Recording(); var request = Request(MediaOperation.CompressVideo, MediaTargets.Mp4, ".mp4", ("TargetSizeMb", "2"), ("KeepAudio", "true"));
        request = request with { InputDetails = [Info() with { Duration = TimeSpan.FromSeconds(60) }] };
        await new FFmpegMediaEngine(fake).ProcessAsync(_dir.Write("source.mp4", "x"), _dir.File("compressed.mp4"), request, default);
        var args = Assert.Single(fake.Calls);
        Assert.Equal("122666", args[args.IndexOf("-b:v") + 1]);
        Assert.Equal("122666", args[args.IndexOf("-maxrate") + 1]);
        Assert.Equal("128k", args[args.IndexOf("-b:a") + 1]);
        Assert.Contains("-protocol_whitelist", args); Assert.Contains("-n", args);
    }
    [Fact]
    public async Task TrimBuildsFastAndPreciseArgumentVectors()
    {
        var fake = new Recording(); var engine = new FFmpegMediaEngine(fake); string source = _dir.Write("source.wav", "x"), dest = _dir.File("out.wav");
        await engine.ProcessAsync(source, dest, Request(MediaOperation.Trim, MediaTargets.SameAsSource, ".wav", ("Start", "0.2"), ("End", "0.7")), default);
        var args = fake.Calls.Single();
        Assert.Equal(new[] { "-hide_banner", "-nostdin", "-nostats", "-n", "-ss", "0.2", "-to", "0.7", "-protocol_whitelist", "file", "-i", source, "-c", "copy", dest }, args);
        fake.Calls.Clear(); File.Delete(dest);
        await engine.ProcessAsync(source, dest, Request(MediaOperation.Trim, MediaTargets.SameAsSource, ".wav", ("Start", "0.2"), ("End", "0.7"), ("Precise", "true")), default);
        Assert.Equal(new[] { "-hide_banner", "-nostdin", "-nostats", "-n", "-protocol_whitelist", "file", "-i", source, "-ss", "0.2", "-t", "0.5", "-vn", "-c:a", "pcm_s16le", dest }, fake.Calls.Single());
    }
    [Fact]
    public async Task LoudnessMeasurementsDriveSecondPassAndRejectNonFiniteValues()
    {
        var fake = new Recording(); string source = _dir.Write("source.wav", "x");
        await new FFmpegMediaEngine(fake).ProcessAsync(source, _dir.File("normalized.wav"), Request(MediaOperation.NormalizeAudio, MediaTargets.SameAsSource, ".wav"), default);
        Assert.Equal(2, fake.Calls.Count); Assert.Contains("loudnorm=I=-16:TP=-1.5:LRA=11:print_format=json", fake.Calls[0]);
        Assert.Contains(fake.Calls[1], a => a.Contains("measured_I=-21.3") && a.Contains("measured_TP=-3.2") && a.Contains("measured_LRA=1.2") && a.Contains("measured_thresh=-31.4") && a.Contains("offset=0.1"));
        Assert.Throws<FileProcessingException>(() => FFmpegMediaEngine.ParseLoudness(Recording.Measurements.Replace("-21.3", "-inf")));
    }
    [Theory]
    [InlineData("success")][InlineData("failure")][InlineData("cancel")]
    public async Task GifPaletteIsTemporaryAndRemovedForEveryOutcome(string mode)
    {
        var fake = new Recording { Mode = mode, StopAtCall = 2 }; string source = _dir.Write("source.mp4", "x");
        var run = new FFmpegMediaEngine(fake).ProcessAsync(source, _dir.File("output.gif"), Request(MediaOperation.ToGif, MediaTargets.Gif, ".mp4", ("Fps", "12"), ("Duration", "1"), ("Width", "480")), default);
        if (mode == "success") await run; else await Assert.ThrowsAnyAsync<Exception>(() => run);
        string palette = fake.Calls[0][^1]; Assert.Equal("palette.png", Path.GetFileName(palette)); Assert.False(Directory.Exists(Path.GetDirectoryName(palette)));
        Assert.Contains(fake.Calls[0], a => a.Contains("palettegen"));
        if (mode == "success") Assert.Contains(fake.Calls[1], a => a.Contains("paletteuse"));
    }
    [Theory]
    [InlineData("success")][InlineData("failure")][InlineData("cancel")]
    public async Task CopyMergeEscapesListAndRemovesItForEveryOutcome(string mode)
    {
        string a = _dir.Write("one ' quote.mp4", "x"), b = _dir.Write("two space.mp4", "x"); var fake = new Recording { Mode = mode };
        var request = new MediaRequest(MediaOperation.Merge, MediaTargets.Mp4, ".mp4", [b], InputDetails: [Info(signature: "same"), Info(signature: "same")]);
        var run = new FFmpegMediaEngine(fake).ProcessAsync(a, _dir.File("joined.mp4"), request, default);
        if (mode == "success") await run; else await Assert.ThrowsAnyAsync<Exception>(() => run);
        Assert.Contains("'\\''", fake.ListContent); Assert.Contains("two space.mp4", fake.ListContent); Assert.False(File.Exists(fake.ListPath));
        Assert.Contains("concat", fake.Calls.Single()); Assert.Contains("copy", fake.Calls.Single()); Assert.Contains("-safe", fake.Calls.Single());
    }
    [Fact]
    public async Task DifferingInputsUseScaledConcatFilterAndSilenceForMissingAudio()
    {
        string a = _dir.Write("one.mp4", "x"), b = _dir.Write("two.mp4", "x"); var fake = new Recording();
        var request = new MediaRequest(MediaOperation.Merge, MediaTargets.Mp4, ".mp4", [b], InputDetails: [Info(), Info() with { Width = 320, Height = 240, HasAudio = false }]);
        await new FFmpegMediaEngine(fake).ProcessAsync(a, _dir.File("joined.mp4"), request, default);
        string filter = fake.Calls.Single()[fake.Calls[0].IndexOf("-filter_complex") + 1];
        Assert.Contains("scale=640:360", filter); Assert.Contains("anullsrc", filter); Assert.Contains("concat=n=2:v=1:a=1", filter);
    }
    [Theory]
    [InlineData(".mkv")][InlineData(".mov")]
    public async Task RemoveAudioPreservesContainerAndUsesStreamCopy(string extension)
    {
        var fake = new Recording(); await new FFmpegMediaEngine(fake).ProcessAsync(_dir.Write("source" + extension, "x"), _dir.File("out" + extension), new(MediaOperation.RemoveAudio, MediaTargets.SameAsSource, extension), default);
        Assert.Contains("-0:a", fake.Calls.Single()); Assert.Contains("copy", fake.Calls.Single()); Assert.EndsWith(extension, fake.Calls[0][^1]);
    }
    [Fact]
    public async Task RotationUsesPixelFilterOrClockwiseDisplayMetadata()
    {
        var fake = new Recording(); var engine = new FFmpegMediaEngine(fake); string source = _dir.Write("source.mp4", "x");
        await engine.ProcessAsync(source, _dir.File("rotate.mp4"), Request(MediaOperation.RotateVideo, MediaTargets.Mp4, ".mp4", ("Angle", "90")), default);
        Assert.Contains(fake.Calls[0], a => a.StartsWith("transpose=1")); Assert.Contains("libx264", fake.Calls[0]);
        await engine.ProcessAsync(source, _dir.File("metadata.mp4"), Request(MediaOperation.RotateVideo, MediaTargets.SameAsSource, ".mp4", ("Angle", "90"), ("MetadataOnly", "true")), default);
        Assert.Contains("-display_rotation", fake.Calls[1]); Assert.Contains("-90", fake.Calls[1]); Assert.Contains("copy", fake.Calls[1]); Assert.DoesNotContain("-vf", fake.Calls[1]);
    }
    [Fact]
    public async Task FrameExtractionRenamesNumberedOutputsToTimestamps()
    {
        var fake = new Recording(); string folder = Directory.CreateDirectory(_dir.File("frames")).FullName;
        await new FFmpegMediaEngine(fake).ProcessAsync(_dir.Write("source.mp4", "x"), folder,
            Request(MediaOperation.ExtractFrames, MediaTargets.Frames, ".mp4", ("Mode", "interval"), ("EverySeconds", "0.5"), ("At", "ignored-invalid-value"), ("Format", "png"), ("Stem", "clip")), default);
        Assert.True(File.Exists(Path.Combine(folder, "clip - 00m00s500ms.png"))); Assert.Equal(4, Directory.GetFiles(folder).Length); Assert.DoesNotContain(Directory.GetFiles(folder), p => p.Contains("%06d"));
    }
    private sealed class Recording : IFFmpegService
    {
        public const string Measurements = """{"input_i":"-21.3","input_tp":"-3.2","input_lra":"1.2","input_thresh":"-31.4","target_offset":"0.1"}""";
        public bool IsAvailable => true; public string? ExecutablePath => null; public string Mode { get; init; } = "success"; public int StopAtCall { get; init; } = 1;
        public List<List<string>> Calls { get; } = []; public string? ListContent { get; private set; } public string? ListPath { get; private set; }
        public Task<string?> GetVersionAsync(CancellationToken ct) => Task.FromResult<string?>("fake");
        public Task<FFmpegResult> ExecuteAsync(IEnumerable<string> arguments, CancellationToken ct)
        {
            var args = arguments.ToList(); Calls.Add(args);
            if (args.Contains("concat")) { ListPath = args[args.IndexOf("-i") + 1]; ListContent = File.ReadAllText(ListPath); }
            if (Mode == "cancel" && Calls.Count >= StopAtCall) throw new OperationCanceledException();
            if (Mode == "failure" && Calls.Count >= StopAtCall) return Task.FromResult(new FFmpegResult(false, "", "private-path", 1));
            string destination = args[^1];
            if (destination.Contains("%06d"))
            {
                int count = int.Parse(args[args.IndexOf("-frames:v") + 1]);
                for (int i = 0; i < count; i++) File.WriteAllText(destination.Replace("%06d", i.ToString("000000")), "frame");
            }
            else if (destination != "-") File.WriteAllText(destination, "output");
            return Task.FromResult(new FFmpegResult(true, "", Measurements, 0));
        }
    }
}
