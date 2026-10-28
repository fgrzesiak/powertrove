using PowerTrove.Application.Media;
using PowerTrove.Abstractions.Files;
using PowerTrove.Abstractions.Testing;
using PowerTrove.Pack.Media;
using PowerTrove.Pack.Media.Windows;
using PowerTrove.Platform.Windows.Media;
using PowerTrove.Abstractions.Packs;
using PowerTrove.Infrastructure.Files;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace PowerTrove.Desktop.Tests;

public sealed class VisualTrimParametersTests : IDisposable
{
    private readonly TempDirectory _directory = new();
    public void Dispose() => _directory.Dispose();
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task VisualRangeExportsThroughTheRealFactoryAndPreservesAudioVideoAndSource(bool video)
    {
        string wav = MediaFixtures.WriteSineWav(_directory.File("range.wav"), 4), input = wav;
        if (video)
        {
            input = _directory.File("range.mp4"); using (File.Create(input)) { }
            var composition = new MediaComposition();
            composition.Clips.Add(MediaClip.CreateFromColor(global::Windows.UI.Color.FromArgb(255, 30, 80, 180), TimeSpan.FromSeconds(2)));
            composition.Clips.Add(MediaClip.CreateFromColor(global::Windows.UI.Color.FromArgb(255, 180, 50, 40), TimeSpan.FromSeconds(2)));
            composition.BackgroundAudioTracks.Add(await BackgroundAudioTrack.CreateFromFileAsync(await StorageFile.GetFileFromPathAsync(wav)));
            Assert.Equal(TranscodeFailureReason.None, await composition.RenderToFileAsync(await StorageFile.GetFileFromPathAsync(input), MediaTrimmingPreference.Precise, MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Vga)));
        }
        byte[] original = File.ReadAllBytes(input);
        var range = new MediaTrimRange(TimeSpan.FromSeconds(4), new Dictionary<string, string> { ["Precise"] = "true" });
        range.SetStart(.75); range.SetEnd(2.75);
        var action = new MediaPackFactory().Create(new PackHostServices(new Version(0, 1, 0), FileActionServices.CreatePhysical(new FileSignatureVerifier())))
            .Actions.Single(a => a.Descriptor.Id == "powertrove.media.trim");
        var context = Snapshots.ForPaths(input); var options = range.Parameters();
        using (var preview = new LocalMediaPlayback(new FileSignatureVerifier()) { IsMuted = true })
        {
            var playable = await preview.OpenAsync(input, default);
            var full = new MediaTrimRange(playable.Duration, new Dictionary<string, string>()).Parameters();
            Assert.Empty(full.Values["End"]);
            Assert.False((await action.PreviewAsync(context, full, default)).IsBlocked);
        }
        Assert.False((await action.PreviewAsync(context, options, default)).IsBlocked);
        var result = await action.ExecuteAsync(context, options, null, default); Assert.True(result.IsSuccess, result.Message);
        string output = Assert.Single(result.OutputPaths); Assert.NotEqual(input, output);
        var info = await new WindowsMediaInspector().InspectAsync(output, default);
        Assert.InRange(info.Duration.TotalSeconds, 1.8, 2.2); Assert.True(info.HasAudio); Assert.Equal(video, info.HasVideo);
        Assert.Equal(original, File.ReadAllBytes(input));
        var outside = Snapshots.Parameters(("Start", "9"), ("End", "10"));
        Assert.True((await action.PreviewAsync(context, outside, default)).IsBlocked);
    }
}
