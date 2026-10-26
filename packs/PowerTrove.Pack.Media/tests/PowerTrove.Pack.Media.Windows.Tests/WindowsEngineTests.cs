using PowerTrove.Pack.Media;
using PowerTrove.Pack.Media.Windows;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace PowerTrove.Platform.Windows.Tests;

/// <summary>
/// Integration tests against the real Windows APIs (WIC, Windows.Data.Pdf, Media Foundation).
/// They run on Windows only and are skipped elsewhere.
/// </summary>
public sealed class WindowsEngineTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private static async Task<byte[]> EncodeAsync(Guid encoderId, int width, int height, byte alpha = 255)
    {
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 200; pixels[i + 1] = 80; pixels[i + 2] = 40; pixels[i + 3] = alpha;
        }
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(encoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, (uint)width, (uint)height, 96, 96, pixels);
        await encoder.FlushAsync();
        var bytes = new byte[stream.Size];
        stream.Seek(0);
        await stream.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);
        return bytes;
    }

    private static async Task<BitmapDecoder> DecodeAsync(byte[] data)
    {
        var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(data.AsBuffer());
        stream.Seek(0);
        return await BitmapDecoder.CreateAsync(stream);
    }

    [Theory]
    [InlineData(MediaTargets.Mp3)]
    [InlineData(MediaTargets.M4a)]
    [InlineData(MediaTargets.Flac)]
    public async Task MediaFoundation_ConvertsSynthesizedWav(string target)
    {
        if (!OperatingSystem.IsWindows() || !new WindowsMediaEngine().IsAvailable) return;
        string wav = MediaFixtures.WriteSineWav(_dir.File("tone.wav"));
        string destination = _dir.File("tone." + target);

        await new WindowsMediaEngine().ProcessAsync(wav, destination, new MediaRequest(MediaOperation.ConvertAudio, target, ".wav"), CancellationToken.None);

        Assert.True(new FileInfo(destination).Length > 500);
    }

    [Fact]
    public async Task MediaFoundation_ExtractsAudioFromVideo_WhenFixtureAvailable()
    {
        if (!OperatingSystem.IsWindows() || !new WindowsMediaEngine().IsAvailable) return;
        string? video = MediaFixtures.TryGenerateVideoWithAudio(_dir.File("clip.mp4"));
        if (video is null) return; // fixture generation needs FFmpeg

        string mp3 = _dir.File("clip.mp3");
        await new WindowsMediaEngine().ProcessAsync(video, mp3, new MediaRequest(MediaOperation.ExtractAudio, MediaTargets.Mp3, ".mp4"), CancellationToken.None);
        Assert.True(new FileInfo(mp3).Length > 500);

        string muted = _dir.File("clip.muted.mp4");
        await new WindowsMediaEngine().ProcessAsync(video, muted, new MediaRequest(MediaOperation.RemoveAudio, MediaTargets.SameAsSource, ".mp4"), CancellationToken.None);
        Assert.False(MediaFixtures.HasAudioStream(muted));
    }

    [Fact]
    public void MediaFoundation_DeclaresFFmpegOnlyFormatsAsUnsupported()
    {
        var engine = new WindowsMediaEngine();
        Assert.False(engine.Supports(new MediaRequest(MediaOperation.ConvertVideo, MediaTargets.WebM, ".mp4")));
        Assert.False(engine.Supports(new MediaRequest(MediaOperation.ConvertAudio, MediaTargets.Mp3, ".ogg")));
        Assert.True(engine.Supports(new MediaRequest(MediaOperation.ConvertAudio, MediaTargets.Mp3, ".flac")));
    }
}
