namespace PowerTrove.Pack.Media.Tests;

public sealed class MediaRealEngineTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    public void Dispose() => _dir.Dispose();
    [Fact]
    public async Task OptionalFfmpegNormalizationMeetsMeasuredTargetWithoutFfprobe()
    {
        var service = new FFmpegService(); if (!service.IsAvailable) return;
        string source = MediaFixtures.WriteSineWav(_dir.File("measurement-source.wav"), 3);
        string output = _dir.File("measured-normalized.wav");
        await new FFmpegMediaEngine(service).ProcessAsync(source, output, new(MediaOperation.NormalizeAudio, MediaTargets.SameAsSource, ".wav"), default);
        var measurement = await service.ExecuteAsync(["-hide_banner", "-nostdin", "-nostats", "-protocol_whitelist", "file", "-i", output, "-af", "loudnorm=I=-16:TP=-1.5:LRA=11:print_format=json", "-f", "null", "-"], default);
        Assert.True(measurement.Success);
        Assert.InRange(FFmpegMediaEngine.ParseLoudness(measurement.Error).Integrated, -16.3, -15.7);
    }
    [Fact]
    public async Task OptionalFfmpegNormalizesAndTrimsGeneratedWav()
    {
        var service = new FFmpegService(); if (!service.IsAvailable || !service.IsProbeAvailable) return;
        string source = MediaFixtures.WriteSineWav(_dir.File("tone.wav")); var inspector = new FFmpegMediaInspector(service); var info = await inspector.InspectAsync(source, default); var engine = new FFmpegMediaEngine(service);
        string normalized = _dir.File("normalized.wav");
        await engine.ProcessAsync(source, normalized, new(MediaOperation.NormalizeAudio, MediaTargets.SameAsSource, ".wav"), default);
        var normalizedInfo = await inspector.InspectAsync(normalized, default); Assert.InRange(normalizedInfo.Duration.TotalSeconds, .95, 1.05); Assert.True(normalizedInfo.HasAudio);
        string trimmed = _dir.File("trimmed.wav");
        await engine.ProcessAsync(source, trimmed, new(MediaOperation.Trim, MediaTargets.SameAsSource, ".wav", Options: new Dictionary<string, string> { ["Start"] = "0.2", ["End"] = "0.7", ["Precise"] = "true" }, InputDetails: [info]), default);
        Assert.InRange((await inspector.InspectAsync(trimmed, default)).Duration.TotalSeconds, .49, .51);
    }
    [Fact]
    public async Task OptionalFfmpegJoinsGeneratedWavs()
    {
        var service = new FFmpegService(); if (!service.IsAvailable || !service.IsProbeAvailable) return;
        string a = MediaFixtures.WriteSineWav(_dir.File("a.wav")), b = MediaFixtures.WriteSineWav(_dir.File("b.wav")); var inspector = new FFmpegMediaInspector(service);
        var info = new[] { await inspector.InspectAsync(a, default), await inspector.InspectAsync(b, default) }; string output = _dir.File("joined.m4a");
        await new FFmpegMediaEngine(service).ProcessAsync(a, output, new(MediaOperation.Merge, MediaTargets.M4a, ".wav", [b], InputDetails: info), default);
        Assert.InRange((await inspector.InspectAsync(output, default)).Duration.TotalSeconds, 1.9, 2.1);
    }
    [Fact]
    public async Task OptionalFfmpegRotatesAndMutesMkvAndMov()
    {
        var service = new FFmpegService(); if (!service.IsAvailable || !service.IsProbeAvailable) return;
        string? source = MediaFixtures.TryGenerateVideoWithAudio(_dir.File("source.mp4")); if (source is null) return;
        var engine = new FFmpegMediaEngine(service); var inspector = new FFmpegMediaInspector(service); var original = await inspector.InspectAsync(source, default);
        string rotated = _dir.File("rotated.mp4");
        await engine.ProcessAsync(source, rotated, new(MediaOperation.RotateVideo, MediaTargets.Mp4, ".mp4", Options: new Dictionary<string, string> { ["Angle"] = "90" }), default);
        var info = await inspector.InspectAsync(rotated, default); Assert.Equal(original.Height, info.Width); Assert.Equal(original.Width, info.Height);
        foreach (string extension in new[] { ".mkv", ".mov" })
        {
            string input = _dir.File("input" + extension); var remux = await service.ExecuteAsync(["-nostdin", "-n", "-protocol_whitelist", "file", "-i", source, "-c", "copy", input], default); Assert.True(remux.Success);
            string muted = _dir.File("muted" + extension); await engine.ProcessAsync(input, muted, new(MediaOperation.RemoveAudio, MediaTargets.SameAsSource, extension), default);
            Assert.False((await inspector.InspectAsync(muted, default)).HasAudio);
        }
    }
}
