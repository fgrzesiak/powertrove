using System.Runtime.InteropServices.WindowsRuntime;
using PowerTrove.Abstractions.Packs;
using PowerTrove.Infrastructure.Files;
using PowerTrove.Pack.Media;
using PowerTrove.Pack.Media.Windows;
using Windows.Graphics.Imaging;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace PowerTrove.Pack.Media.Windows.Tests;

public sealed class ConversionPairTests : IDisposable
{
    private readonly TempDirectory _directory = new();
    public void Dispose() => _directory.Dispose();

    [Fact]
    public async Task GeneratedMp4HonorsTargetSizeThroughTheRealWindowsCompressionEngine()
    {
        string path = _directory.File("target-size.mp4"); using (File.Create(path)) { }
        var composition = new MediaComposition();
        composition.Clips.Add(MediaClip.CreateFromColor(global::Windows.UI.Color.FromArgb(255, 30, 80, 180), TimeSpan.FromSeconds(4)));
        string wav = MediaFixtures.WriteSineWav(_directory.File("target-tone.wav"), 4);
        composition.BackgroundAudioTracks.Add(await BackgroundAudioTrack.CreateFromFileAsync(await StorageFile.GetFileFromPathAsync(wav)));
        var rendered = await composition.RenderToFileAsync(await StorageFile.GetFileFromPathAsync(path), MediaTrimmingPreference.Precise, MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Vga));
        Assert.Equal(TranscodeFailureReason.None, rendered);
        // A standards-shaped MP4 free box makes a real size reduction useful even
        // for this small, deterministic color clip; already-smaller outputs may be skipped.
        var free = new byte[2 * 1024 * 1024];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(free.AsSpan(0, 4), (uint)free.Length);
        System.Text.Encoding.ASCII.GetBytes("free").CopyTo(free, 4);
        await using (var padding = new FileStream(path, FileMode.Append, FileAccess.Write)) await padding.WriteAsync(free);
        byte[] original = File.ReadAllBytes(path); Assert.True(original.Length > 1000000);
        var host = new PackHostServices(new Version(0, 1, 0), FileActionServices.CreatePhysical(new FileSignatureVerifier()));
        var action = new MediaPackFactory().Create(host).Actions.Single(a => a.Descriptor.Id == "powertrove.video.compress");
        var parameters = Snapshots.Parameters(("TargetSizeMb", "1")); var context = Snapshots.ForPaths(path);
        Assert.False((await action.PreviewAsync(context, parameters, default)).IsBlocked);
        var result = await action.ExecuteAsync(context, parameters, null, default); Assert.True(result.IsSuccess, result.Message);
        string output = Assert.Single(result.OutputPaths); Assert.InRange(new FileInfo(output).Length, 1, 1000000);
        var info = await new WindowsMediaInspector().InspectAsync(output, default); Assert.True(info.HasVideo && info.HasAudio); Assert.InRange(info.Duration.TotalSeconds, 3.8, 4.2);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task GeneratedMp4AudioAndGifUseRealPackAndDecodableOutputs()
    {
        string path = _directory.File("generated.mp4");
        using (File.Create(path)) { }
        var composition = new MediaComposition();
        composition.Clips.Add(MediaClip.CreateFromColor(global::Windows.UI.Color.FromArgb(255, 30, 80, 180), TimeSpan.FromSeconds(1.2)));
        string wav = MediaFixtures.WriteSineWav(_directory.File("tone.wav"), 1.2);
        composition.BackgroundAudioTracks.Add(await BackgroundAudioTrack.CreateFromFileAsync(await StorageFile.GetFileFromPathAsync(wav)));
        var target = await StorageFile.GetFileFromPathAsync(path);
        var rendered = await composition.RenderToFileAsync(target, MediaTrimmingPreference.Precise, MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Vga));
        Assert.Equal(TranscodeFailureReason.None, rendered);
        byte[] original = File.ReadAllBytes(path);
        var host = new PackHostServices(new Version(0, 1, 0), FileActionServices.CreatePhysical(new FileSignatureVerifier()));
        var pack = new MediaPackFactory().Create(host);
        var audio = pack.Actions.Single(a => a.Descriptor.Id == "powertrove.video.extract-audio");
        var receipt = await audio.ExecuteAsync(Snapshots.ForPaths(path), new EmptyParameters(), null, default);
        Assert.True(receipt.IsSuccess, receipt.Message);
        var info = await new WindowsMediaInspector().InspectAsync(Assert.Single(receipt.OutputPaths), default);
        Assert.True(info.HasAudio); Assert.False(info.HasVideo); Assert.InRange(info.Duration.TotalSeconds, 1, 1.5);
        var gif = pack.Actions.Single(a => a.Descriptor.Id == "powertrove.video.to-gif");
        var options = Snapshots.Parameters(("Duration", "1"), ("Fps", "5"), ("Width", "160"));
        Assert.False((await gif.PreviewAsync(Snapshots.ForPaths(path), options, default)).IsBlocked);
        var gifReceipt = await gif.ExecuteAsync(Snapshots.ForPaths(path), options, null, default);
        Assert.True(gifReceipt.IsSuccess, gifReceipt.Message);
        using var input = File.OpenRead(Assert.Single(gifReceipt.OutputPaths));
        var decoded = await BitmapDecoder.CreateAsync(input.AsRandomAccessStream());
        Assert.Equal(BitmapDecoder.GifDecoderId, decoded.DecoderInformation.CodecId);
        Assert.Equal(5u, decoded.FrameCount); Assert.Equal(160u, decoded.PixelWidth);
        Assert.Equal(original, File.ReadAllBytes(path));

        // QuickTime uses the same file-type atom layout as MPEG-4. Mark this generated
        // H.264/AAC subset as QuickTime-compatible, rather than merely renaming an MP4.
        // https://developer.apple.com/documentation/quicktime-file-format/file_type_compatibility_atom
        byte[] movBytes = original.ToArray();
        Assert.Equal("ftyp", System.Text.Encoding.ASCII.GetString(movBytes, 4, 4));
        Assert.True(System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(movBytes.AsSpan(0, 4)) >= 20);
        System.Text.Encoding.ASCII.GetBytes("qt  ").CopyTo(movBytes, 8);
        System.Text.Encoding.ASCII.GetBytes("qt  ").CopyTo(movBytes, 16);
        string mov = _directory.Write("quicktime.mov", movBytes);
        var convert = pack.Actions.Single(a => a.Descriptor.Id == "powertrove.video.convert-mp4");
        var converted = await convert.ExecuteAsync(Snapshots.ForPaths(mov), new EmptyParameters(), null, default);
        Assert.True(converted.IsSuccess, converted.Message);
        var convertedInfo = await new WindowsMediaInspector().InspectAsync(Assert.Single(converted.OutputPaths), default);
        Assert.True(convertedInfo.HasVideo && convertedInfo.HasAudio);
        Assert.InRange(convertedInfo.Duration.TotalSeconds, 1, 1.5);
        Assert.Equal(movBytes, File.ReadAllBytes(mov));
    }
}
