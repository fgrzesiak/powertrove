namespace PowerTrove.Pack.Media.Tests;

public sealed class FFmpegServiceDiscoveryTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void MissingFFmpeg_IsLookedUpAgain_SoAGuidedInstallationNeedsNoRestart()
    {
        string? installed = null;
        int lookups = 0;
        var service = new FFmpegService(null, () => { lookups++; return installed; }, lookupIntervalMs: 0);

        Assert.False(service.IsAvailable);
        installed = _dir.Write("ffmpeg.exe", [0]);

        Assert.True(service.IsAvailable);
        Assert.Equal(installed, service.ExecutablePath);
        int afterFound = lookups;
        Assert.True(service.IsAvailable);
        Assert.Equal(afterFound, lookups);
    }

    [Fact]
    public void LookupsWhileMissing_AreThrottled()
    {
        int lookups = 0;
        var service = new FFmpegService(null, () => { lookups++; return null; }, lookupIntervalMs: 60_000);

        for (int i = 0; i < 5; i++) Assert.False(service.IsAvailable);

        Assert.Equal(1, lookups);
    }

    [Fact]
    public void CustomPath_IsNeverReplacedByALookup()
    {
        int lookups = 0;
        string custom = Path.Combine(_dir.Path, "missing", "ffmpeg.exe");
        var service = new FFmpegService(custom, () => { lookups++; return null; }, lookupIntervalMs: 0);

        Assert.False(service.IsAvailable);
        Assert.False(service.IsAvailable);

        Assert.Equal(0, lookups);
        Assert.Equal(custom, service.ExecutablePath);
    }
}
