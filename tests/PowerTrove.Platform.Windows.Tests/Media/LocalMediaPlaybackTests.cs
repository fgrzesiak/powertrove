using PowerTrove.Application.Media;
using PowerTrove.Infrastructure.Files;
using PowerTrove.Platform.Windows.Media;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace PowerTrove.Platform.Windows.Tests.Media;

public sealed class LocalMediaPlaybackTests : IDisposable
{
    private readonly TempDirectory _directory = new();
    public void Dispose() => _directory.Dispose();

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task GeneratedLocalAudioAndVideoOpenSeekPlayAndReleaseTheOriginal(bool video)
    {
        string wav = MediaFixtures.WriteSineWav(_directory.File("preview.wav"), 4);
        string input = wav;
        if (video)
        {
            input = _directory.File("preview.mp4"); using (File.Create(input)) { }
            var composition = new MediaComposition();
            composition.Clips.Add(MediaClip.CreateFromColor(global::Windows.UI.Color.FromArgb(255, 30, 80, 180), TimeSpan.FromSeconds(4)));
            composition.BackgroundAudioTracks.Add(await BackgroundAudioTrack.CreateFromFileAsync(await StorageFile.GetFileFromPathAsync(wav)));
            Assert.Equal(TranscodeFailureReason.None, await composition.RenderToFileAsync(await StorageFile.GetFileFromPathAsync(input), MediaTrimmingPreference.Precise, MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Vga)));
        }
        byte[] original = File.ReadAllBytes(input);
        using (var preview = new LocalMediaPlayback(new FileSignatureVerifier()) { IsMuted = true })
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var info = await preview.OpenAsync(input, timeout.Token);
            Assert.InRange(info.Duration.TotalSeconds, 3.8, 4.2); Assert.Equal(video, info.HasVideo); Assert.False(preview.IsPlaying);
            preview.Seek(TimeSpan.FromSeconds(1.25));
            await WaitFor(() => Math.Abs(preview.Position.TotalSeconds - 1.25) < .15, timeout.Token);
            preview.Play(); await WaitFor(() => preview.Position.TotalSeconds > 1.5, timeout.Token);
            preview.Pause(); Assert.True(preview.Position < info.Duration);
            Assert.Equal(original, File.ReadAllBytes(input));
        }
        using var exclusive = new FileStream(input, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal(original.Length, exclusive.Length);
    }
    [Fact]
    public async Task InvalidSignaturesRemoteInputsAndCancellationFailWithoutPublishingFiles()
    {
        string disguised = _directory.Write("invalid.mp4", "not a media file");
        using (var preview = new LocalMediaPlayback(new FileSignatureVerifier()))
        {
            var error = await Assert.ThrowsAsync<MediaPreviewException>(() => preview.OpenAsync(disguised, default));
            Assert.Equal(MediaPreviewFailure.InvalidInput, error.Failure); Assert.DoesNotContain("invalid.mp4", error.ToString());
        }
        using (var preview = new LocalMediaPlayback(new FileSignatureVerifier()))
        {
            var error = await Assert.ThrowsAsync<MediaPreviewException>(() => preview.OpenAsync("https://example.invalid/clip.mp4", default));
            Assert.Equal(MediaPreviewFailure.InvalidInput, error.Failure);
        }
        string renamed = MediaFixtures.WriteSineWav(_directory.File("wrong-type.mp4"));
        using (var preview = new LocalMediaPlayback(new FileSignatureVerifier()))
            Assert.Equal(MediaPreviewFailure.InvalidInput, (await Assert.ThrowsAsync<MediaPreviewException>(() => preview.OpenAsync(renamed, default))).Failure);
        using (var preview = new LocalMediaPlayback(new FileSignatureVerifier()))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preview.OpenAsync(disguised, new(true)));
        Assert.Empty(_directory.NewFiles(disguised, renamed));
    }
    private static async Task WaitFor(Func<bool> condition, CancellationToken token)
    {
        while (!condition()) await Task.Delay(40, token);
    }
}
