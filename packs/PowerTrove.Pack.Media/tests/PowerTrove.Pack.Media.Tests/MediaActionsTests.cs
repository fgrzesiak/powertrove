using PowerTrove.Infrastructure.Files;
using PowerTrove.Pack.Media;

namespace PowerTrove.Pack.Media.Tests;

public sealed class MediaActionsTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private static FileActionServices Services() => FileActionServices.CreatePhysical(new FileSignatureVerifier());

    private static IAction Action(string id, params IMediaEngine[] engines) =>
        new MediaPack(new CompositeMediaEngine(engines), Services()).Actions.Single(a => a.Descriptor.Id == id);

    [Fact]
    public async Task ConvertAudio_UsesFirstSupportingEngine_AndWritesNextToSource()
    {
        string wav = MediaFixtures.WriteSineWav(_dir.File("tone.wav"));
        var unsupported = new FakeEngine { SupportsAnything = false };
        var engine = new FakeEngine();

        var receipt = await Action("powertrove.audio.convert-mp3", unsupported, engine)
            .ExecuteAsync(Snapshots.ForPaths(wav), new EmptyParameters(), null, CancellationToken.None);

        Assert.True(receipt.IsSuccess, receipt.Message);
        Assert.Equal(_dir.File("tone.mp3"), Assert.Single(receipt.OutputPaths));
        Assert.Equal(new MediaRequest(MediaOperation.ConvertAudio, MediaTargets.Mp3, ".wav"), engine.LastRequest);
        Assert.EndsWith(".mp3", engine.LastDestination);
        Assert.StartsWith(".~cb", Path.GetFileName(engine.LastDestination));
    }

    [Fact]
    public async Task RemoveAudio_KeepsContainerExtension()
    {
        string mp4 = _dir.Write("clip.mp4", IsoMediaHeader());
        var engine = new FakeEngine();

        var receipt = await Action("powertrove.video.remove-audio", engine)
            .ExecuteAsync(Snapshots.ForPaths(mp4), new EmptyParameters(), null, CancellationToken.None);

        Assert.Equal(_dir.File("clip.muted.mp4"), Assert.Single(receipt.OutputPaths));
    }

    [Fact]
    public async Task EngineFailure_LeavesNoPartialOutput()
    {
        string wav = MediaFixtures.WriteSineWav(_dir.File("tone.wav"));
        var engine = new FakeEngine { Fail = true };

        var receipt = await Action("powertrove.audio.convert-flac", engine)
            .ExecuteAsync(Snapshots.ForPaths(wav), new EmptyParameters(), null, CancellationToken.None);

        Assert.False(receipt.IsSuccess);
        Assert.Equal("The local media engine could not process this file.", Assert.Single(receipt.Failures).ErrorMessage);
        Assert.DoesNotContain("Simulated codec failure", receipt.Failures[0].ErrorMessage);
        Assert.Equal(new[] { wav }, _dir.NewFiles());
    }

    [Fact]
    public async Task WebM_WithoutFFmpeg_ExplainsWhy()
    {
        string mp4 = _dir.Write("clip.mp4", IsoMediaHeader());
        var windowsLike = new FakeEngine { Targets = new[] { MediaTargets.Mp4, MediaTargets.Mp3 } };

        var availability = await Action("powertrove.video.convert-webm", windowsLike).MatchAsync(Snapshots.ForPaths(mp4), CancellationToken.None);

        Assert.False(availability.IsAvailable);
        Assert.Contains("FFmpeg", availability.Reason);
    }

    [Fact]
    public async Task Preview_WarnsAboutLossyConversions()
    {
        string wav = MediaFixtures.WriteSineWav(_dir.File("master.wav"));

        var preview = await Action("powertrove.audio.convert-mp3", new FakeEngine())
            .PreviewAsync(Snapshots.ForPaths(wav), new EmptyParameters(), CancellationToken.None);

        Assert.Contains(preview.Warnings, w => w.Contains("lossless format to a lossy format"));
    }

    [Theory]
    [InlineData(MediaOperation.ConvertAudio, MediaTargets.Mp3, "libmp3lame")]
    [InlineData(MediaOperation.ConvertAudio, MediaTargets.M4a, "aac")]
    [InlineData(MediaOperation.ConvertVideo, MediaTargets.Mp4, "libx264")]
    [InlineData(MediaOperation.ConvertVideo, MediaTargets.WebM, "libvpx-vp9")]
    [InlineData(MediaOperation.RemoveAudio, MediaTargets.SameAsSource, "-0:a")]
    public void FFmpegArguments_MapPresets(MediaOperation operation, string target, string expected)
    {
        Assert.Contains(expected, FFmpegMediaEngine.CodecArguments(new MediaRequest(operation, target, ".mp4")));
    }

    [Fact]
    public async Task FFmpeg_ReadsOnlyLocalFilesAndNeverOverwrites()
    {
        var ffmpeg = new RecordingFFmpeg();
        await new FFmpegMediaEngine(ffmpeg).ProcessAsync("C:\\in.wav", "C:\\out.mp3", new MediaRequest(MediaOperation.ConvertAudio, MediaTargets.Mp3, ".wav"), CancellationToken.None);

        var args = ffmpeg.LastArguments!;
        Assert.Contains("-n", args);
        Assert.Equal("file", args[args.IndexOf("-protocol_whitelist") + 1]);
        Assert.Equal("C:\\out.mp3", args[^1]);
    }

    [Fact]
    public async Task FFmpeg_ErrorOutputIsNotShownToUsers()
    {
        var ffmpeg = new RecordingFFmpeg { Result = new FFmpegResult(false, "", "C:\\Users\\secret\\file.wav: Invalid data", 1) };

        var ex = await Assert.ThrowsAsync<FileProcessingException>(() =>
            new FFmpegMediaEngine(ffmpeg).ProcessAsync("C:\\in.wav", "C:\\out.mp3", new MediaRequest(MediaOperation.ConvertAudio, MediaTargets.Mp3, ".wav"), CancellationToken.None));

        Assert.DoesNotContain("secret", ex.Message);
    }

    [Fact]
    public async Task RealFFmpeg_ConvertsWavToMp3_WhenInstalled()
    {
        var service = new FFmpegService();
        if (!service.IsAvailable) return; // FFmpeg is optional; covered by the fake-engine tests above.

        string wav = MediaFixtures.WriteSineWav(_dir.File("tone.wav"));
        var receipt = await Action("powertrove.audio.convert-mp3", new FFmpegMediaEngine(service))
            .ExecuteAsync(Snapshots.ForPaths(wav), new EmptyParameters(), null, CancellationToken.None);

        Assert.True(receipt.IsSuccess, receipt.Failures.FirstOrDefault()?.ErrorMessage);
        Assert.True(new FileInfo(receipt.OutputPaths[0]).Length > 1000);
    }


    [Fact]
    public void ReplaceAudio_IsIdealForOneVideoOneAudio()
    {
        var action = Action("powertrove.video.replace-audio", new FakeEngine());

        Assert.Equal(SelectionFit.Ideal, SelectionRequirements.Evaluate(action.Descriptor.Selection!, Snapshots.ForPaths("v.mp4", "a.mp3")));
        Assert.Equal(SelectionFit.None, SelectionRequirements.Evaluate(action.Descriptor.Selection!, Snapshots.ForPaths("v.mp4", "v2.mp4", "a.mp3")));
        Assert.Equal(SelectionFit.None, SelectionRequirements.Evaluate(action.Descriptor.Selection!, Snapshots.ForPaths("v.mp4")));
    }

    [Fact]
    public async Task ReplaceAudio_ChecksFFmpegArguments()
    {
        var ffmpeg = new RecordingFFmpeg();
        var engine = new FFmpegMediaEngine(ffmpeg);

        await engine.ProcessAsync("C:\\v.mp4", "C:\\out.mp4", new MediaRequest(MediaOperation.ReplaceAudio, MediaTargets.SameAsSource, ".mp4", new[] { "C:\\a.mp3" }), CancellationToken.None);
        var args = ffmpeg.LastArguments!;
        Assert.Contains("-i", args);
        Assert.Equal("C:\\v.mp4", args[args.IndexOf("-i") + 1]);
        Assert.Equal("C:\\a.mp3", args[args.IndexOf("-i", args.IndexOf("-i") + 1) + 1]);
        Assert.Contains("-map", args);
        Assert.Contains("0:v", args);
        Assert.Contains("1:a", args);
        Assert.Contains("copy", args);
        Assert.Contains("aac", args);
        Assert.Contains("-shortest", args);

        await engine.ProcessAsync("C:\\v.mkv", "C:\\out.mkv", new MediaRequest(MediaOperation.ReplaceAudio, MediaTargets.SameAsSource, ".mkv", new[] { "C:\\a.mp3" }), CancellationToken.None);
        var argsMkv = ffmpeg.LastArguments!;
        Assert.Contains("-c:a", argsMkv);
        Assert.Equal("copy", argsMkv[argsMkv.IndexOf("-c:a") + 1]);
    }

    [Fact]
    public async Task ReplaceAudio_WithoutFFmpeg_IsUnavailable()
    {
        var windowsLike = new FakeEngine { Targets = new[] { MediaTargets.Mp4 } };
        var availability = await Action("powertrove.video.replace-audio", windowsLike).MatchAsync(Snapshots.ForPaths("v.mp4", "a.mp3"), CancellationToken.None);

        Assert.False(availability.IsAvailable);
        Assert.Contains("FFmpeg", availability.Reason);
    }


    [Fact]
    public void EmbedSubtitles_IsIdealForOneVideoOneToThreeSubtitles()
    {
        var action = Action("powertrove.video.embed-subtitles", new FakeEngine());

        Assert.Equal(SelectionFit.Ideal, SelectionRequirements.Evaluate(action.Descriptor.Selection!, Snapshots.ForPaths("v.mp4", "s1.srt")));
        Assert.Equal(SelectionFit.Ideal, SelectionRequirements.Evaluate(action.Descriptor.Selection!, Snapshots.ForPaths("v.mp4", "s1.srt", "s2.vtt", "s3.ass")));
        Assert.Equal(SelectionFit.None, SelectionRequirements.Evaluate(action.Descriptor.Selection!, Snapshots.ForPaths("v.mp4", "s1.srt", "s2.vtt", "s3.ass", "s4.ssa")));
        Assert.Equal(SelectionFit.None, SelectionRequirements.Evaluate(action.Descriptor.Selection!, Snapshots.ForPaths("v.mp4", "v2.mp4", "s1.srt")));
        Assert.Equal(SelectionFit.None, SelectionRequirements.Evaluate(action.Descriptor.Selection!, Snapshots.ForPaths("v.mp4")));
    }

    [Fact]
    public async Task EmbedSubtitles_ChecksFFmpegArguments_Mp4()
    {
        var ffmpeg = new RecordingFFmpeg();
        var engine = new FFmpegMediaEngine(ffmpeg);

        await engine.ProcessAsync("C:\\v.mp4", "C:\\out.mp4", new MediaRequest(MediaOperation.EmbedSubtitles, MediaTargets.SameAsSource, ".mp4", new[] { "C:\\s1.de.srt", "C:\\s2.en.vtt" }), CancellationToken.None);
        var args = ffmpeg.LastArguments!;
        Assert.Contains("-i", args);
        Assert.Equal("C:\\s1.de.srt", args[args.IndexOf("-i", args.IndexOf("-i") + 1) + 1]);
        Assert.Contains("-map", args);
        Assert.Contains("0", args);
        Assert.Contains("1", args);
        Assert.Contains("2", args);
        Assert.Contains("-c", args);
        Assert.Contains("copy", args);
        Assert.Contains("-c:s", args);
        Assert.Contains("mov_text", args);

        Assert.Contains("-metadata:s:s:0", args);
        Assert.Equal("language=de", args[args.IndexOf("-metadata:s:s:0") + 1]);
        Assert.Contains("-metadata:s:s:1", args);
        Assert.Equal("language=en", args[args.IndexOf("-metadata:s:s:1") + 1]);
    }

    [Fact]
    public async Task EmbedSubtitles_ChecksFFmpegArguments_Mkv()
    {
        var ffmpeg = new RecordingFFmpeg();
        var engine = new FFmpegMediaEngine(ffmpeg);

        await engine.ProcessAsync("C:\\v.mkv", "C:\\out.mkv", new MediaRequest(MediaOperation.EmbedSubtitles, MediaTargets.SameAsSource, ".mkv", new[] { "C:\\s1.srt" }), CancellationToken.None);
        var args = ffmpeg.LastArguments!;
        Assert.Contains("-i", args);
        Assert.Equal("C:\\s1.srt", args[args.IndexOf("-i", args.IndexOf("-i") + 1) + 1]);
        Assert.Contains("-map", args);
        Assert.Contains("0", args);
        Assert.Contains("1", args);
        Assert.Contains("-c", args);
        Assert.Contains("copy", args);
        Assert.DoesNotContain("-c:s", args);
        Assert.DoesNotContain("mov_text", args);
    }

    [Fact]
    public async Task EmbedSubtitles_WithoutFFmpeg_IsUnavailable()
    {
        var windowsLike = new FakeEngine { Targets = new[] { MediaTargets.Mp4 } };
        var availability = await Action("powertrove.video.embed-subtitles", windowsLike).MatchAsync(Snapshots.ForPaths("v.mp4", "s.srt"), CancellationToken.None);

        Assert.False(availability.IsAvailable);
        Assert.Contains("FFmpeg", availability.Reason);
    }


    [Fact]
    public void StillImageVideo_IsIdealForOneImageOneAudio()
    {
        var action = Action("powertrove.video.still-image", new FakeEngine());

        Assert.Equal(SelectionFit.Ideal, SelectionRequirements.Evaluate(action.Descriptor.Selection!, Snapshots.ForPaths("i.jpg", "a.mp3")));
        Assert.Equal(SelectionFit.None, SelectionRequirements.Evaluate(action.Descriptor.Selection!, Snapshots.ForPaths("i.jpg", "i2.jpg", "a.mp3")));
        Assert.Equal(SelectionFit.None, SelectionRequirements.Evaluate(action.Descriptor.Selection!, Snapshots.ForPaths("i.jpg")));
        Assert.Equal(SelectionFit.None, SelectionRequirements.Evaluate(action.Descriptor.Selection!, Snapshots.ForPaths("a.mp3")));
    }

    [Fact]
    public async Task StillImageVideo_ChecksFFmpegArguments()
    {
        var ffmpeg = new RecordingFFmpeg();
        var engine = new FFmpegMediaEngine(ffmpeg);

        await engine.ProcessAsync("C:\\i.jpg", "C:\\out.mp4", new MediaRequest(MediaOperation.StillImageVideo, MediaTargets.Mp4, ".jpg", new[] { "C:\\a.mp3" }), CancellationToken.None);
        var args = ffmpeg.LastArguments!;
        Assert.Contains("-loop", args);
        Assert.Equal("1", args[args.IndexOf("-loop") + 1]);
        Assert.Contains("-i", args);
        Assert.Equal("C:\\i.jpg", args[args.IndexOf("-i") + 1]);
        Assert.Equal("C:\\a.mp3", args[args.IndexOf("-i", args.IndexOf("-i") + 1) + 1]);
        Assert.Contains("-c:v", args);
        Assert.Equal("libx264", args[args.IndexOf("-c:v") + 1]);
        Assert.Contains("-tune", args);
        Assert.Equal("stillimage", args[args.IndexOf("-tune") + 1]);
        Assert.Contains("-pix_fmt", args);
        Assert.Equal("yuv420p", args[args.IndexOf("-pix_fmt") + 1]);
        Assert.Contains("-vf", args);
        Assert.Equal("scale='min(iw,1920)':'min(ih,1920)':force_original_aspect_ratio=decrease,pad=ceil(iw/2)*2:ceil(ih/2)*2", args[args.IndexOf("-vf") + 1]);
        Assert.Contains("-c:a", args);
        Assert.Equal("aac", args[args.IndexOf("-c:a") + 1]);
        Assert.Contains("-shortest", args);
    }

    [Fact]
    public async Task StillImageVideo_WithoutFFmpeg_IsUnavailable()
    {
        // Like WindowsMediaEngine: MP4 output, but no still-image composition.
        var windowsLike = new FakeEngine { Targets = new[] { MediaTargets.Mp4 }, Unsupported = new[] { MediaOperation.StillImageVideo } };
        var availability = await Action("powertrove.video.still-image", windowsLike).MatchAsync(Snapshots.ForPaths("i.jpg", "a.mp3"), CancellationToken.None);

        Assert.False(availability.IsAvailable);
        Assert.Contains("FFmpeg", availability.Reason);
    }

    private static byte[] IsoMediaHeader() =>
        new byte[] { 0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'i', (byte)'s', (byte)'o', (byte)'m', 0, 0, 2, 0, (byte)'i', (byte)'s', (byte)'o', (byte)'m', (byte)'m', (byte)'p', (byte)'4', (byte)'1', 0, 0, 0, 8 };

    private sealed class FakeEngine : IMediaEngine
    {
        public bool SupportsAnything { get; init; } = true;
        public bool Fail { get; init; }
        public string[]? Targets { get; init; }
        public MediaOperation[] Unsupported { get; init; } = Array.Empty<MediaOperation>();
        public MediaRequest? LastRequest { get; private set; }
        public string LastDestination { get; private set; } = string.Empty;
        public string Name => "fake";
        public bool IsAvailable => true;
        public bool Supports(MediaRequest request) => SupportsAnything && !Unsupported.Contains(request.Operation)
            && (Targets is null || Targets.Contains(request.Target));

        public async Task ProcessAsync(string sourcePath, string destinationPath, MediaRequest request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastDestination = destinationPath;
            await File.WriteAllTextAsync(destinationPath, "partial", cancellationToken);
            if (Fail) throw new FileProcessingException("Simulated codec failure.");
        }
    }

    private sealed class RecordingFFmpeg : IFFmpegService
    {
        public List<string>? LastArguments { get; private set; }
        public FFmpegResult Result { get; init; } = new(true, "", "", 0);
        public bool IsAvailable => true;
        public string? ExecutablePath => "ffmpeg";
        public Task<string?> GetVersionAsync(CancellationToken cancellationToken) => Task.FromResult<string?>("test");

        public Task<FFmpegResult> ExecuteAsync(IEnumerable<string> arguments, CancellationToken cancellationToken)
        {
            LastArguments = arguments.ToList();
            return Task.FromResult(Result);
        }
    }
}
