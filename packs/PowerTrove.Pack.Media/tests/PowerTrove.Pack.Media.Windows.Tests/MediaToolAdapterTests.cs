using PowerTrove.Pack.Media;
using PowerTrove.Pack.Media.Windows;
using Windows.Graphics.Imaging;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace PowerTrove.Platform.Windows.Tests;

public sealed class MediaToolAdapterTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    public void Dispose() => _dir.Dispose();
    [Fact]
    public async Task NativeInspectorAndTrimReadSynthesizedWav()
    {
        if (!OperatingSystem.IsWindows() || !new WindowsMediaEngine().IsAvailable) return;
        string source = MediaFixtures.WriteSineWav(_dir.File("source.wav")); var inspector = new WindowsMediaInspector();
        var info = await inspector.InspectAsync(source, default);
        Assert.InRange(info.Duration.TotalSeconds, .99, 1.01); Assert.Equal(1, info.Channels); Assert.True(info.HasAudio);
        string output = _dir.File("trimmed.wav");
        await new WindowsMediaEngine().ProcessAsync(source, output, new(MediaOperation.Trim, MediaTargets.SameAsSource, ".wav", Options: new Dictionary<string, string> { ["Start"] = "0.2", ["End"] = "0.7" }, InputDetails: [info]), default);
        Assert.InRange((await inspector.InspectAsync(output, default)).Duration.TotalSeconds, .45, .55);
    }
    [Fact]
    public async Task NativeAudioCompositionJoinsTwoGeneratedWavs()
    {
        if (!OperatingSystem.IsWindows() || !new WindowsMediaEngine().IsAvailable) return;
        string first = MediaFixtures.WriteSineWav(_dir.File("first.wav"), .6), second = MediaFixtures.WriteSineWav(_dir.File("second.wav"), .8); var inspector = new WindowsMediaInspector();
        var info = new[] { await inspector.InspectAsync(first, default), await inspector.InspectAsync(second, default) };
        string output = _dir.File("joined.m4a");
        await new WindowsMediaEngine().ProcessAsync(first, output, new(MediaOperation.Merge, MediaTargets.M4a, ".wav", [second], InputDetails: info), default);
        Assert.InRange((await inspector.InspectAsync(output, default)).Duration.TotalSeconds, 1.3, 1.5);
    }
    [Fact]
    public async Task NativeVideoGifFramesAndCompressionUseGeneratedClip()
    {
        if (!OperatingSystem.IsWindows() || !new WindowsMediaEngine().IsAvailable) return;
        string? source = await TryGenerateVideoAsync(); if (source is null) return;
        var inspector = new WindowsMediaInspector(); var info = await inspector.InspectAsync(source, default); var engine = new WindowsMediaEngine();
        string gif = _dir.File("clip.gif");
        await engine.ProcessAsync(source, gif, new(MediaOperation.ToGif, MediaTargets.Gif, ".mp4", Options: new Dictionary<string, string> { ["Start"] = "0", ["Duration"] = "1", ["Fps"] = "5", ["Width"] = "160" }, InputDetails: [info]), default);
        using (var stream = new FileStream(gif, FileMode.Open, FileAccess.Read))
        using (var random = stream.AsRandomAccessStream()) { var decoder = await BitmapDecoder.CreateAsync(random); Assert.Equal(5u, decoder.FrameCount); }
        string frames = Directory.CreateDirectory(_dir.File("frames")).FullName;
        await engine.ProcessAsync(source, frames, new(MediaOperation.ExtractFrames, MediaTargets.Frames, ".mp4", Options: new Dictionary<string, string> { ["Mode"] = "single", ["At"] = "0.5", ["Format"] = "png", ["Stem"] = "clip" }, InputDetails: [info]), default);
        using (var stream = File.OpenRead(Assert.Single(Directory.GetFiles(frames))))
        using (var random = stream.AsRandomAccessStream()) { var decoder = await BitmapDecoder.CreateAsync(random); Assert.Equal((uint)info.Width!, decoder.PixelWidth); Assert.Equal((uint)info.Height!, decoder.PixelHeight); }
        string compressed = _dir.File("compressed.mp4");
        await engine.ProcessAsync(source, compressed, new(MediaOperation.CompressVideo, MediaTargets.Mp4, ".mp4", Options: new Dictionary<string, string> { ["Quality"] = "small", ["KeepAudio"] = "false" }, InputDetails: [info]), default);
        var output = await inspector.InspectAsync(compressed, default); Assert.True(output.Width <= info.Width && output.Height <= info.Height); Assert.False(output.HasAudio);
    }
    private async Task<string?> TryGenerateVideoAsync()
    {
        string path = _dir.File("fixture.mp4");
        try
        {
            using (File.Create(path)) { }
            var output = await StorageFile.GetFileFromPathAsync(path);
            var composition = new MediaComposition(); composition.Clips.Add(MediaClip.CreateFromColor(global::Windows.UI.Color.FromArgb(255, 100, 149, 237), TimeSpan.FromSeconds(1.2)));
            var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Vga); profile.Audio = null;
            return await composition.RenderToFileAsync(output, MediaTrimmingPreference.Precise, profile) == TranscodeFailureReason.None ? path : null;
        }
        catch (System.Runtime.InteropServices.COMException) { return null; } // Native codec unavailable; do not require FFmpeg for this fixture.
    }
}
