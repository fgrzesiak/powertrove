using PowerTrove.Pack.Media.Windows;
using PowerTrove.Abstractions.Packs;

[assembly: ActionPackFactory(typeof(PowerTrove.Pack.Media.MediaPackFactory))]

namespace PowerTrove.Pack.Media;

public sealed class MediaPackFactory : IPackFactory
{
    private readonly CompositeMediaEngine? _engine;
    public MediaPackFactory() { }
    public MediaPackFactory(CompositeMediaEngine engine) => _engine = engine ?? throw new ArgumentNullException(nameof(engine));
    public ActionPackInfo Info => MediaPack.Describe();
    public IActionPack Create(IPackHostServices host)
    {
        ArgumentNullException.ThrowIfNull(host);
        var ffmpeg = new FFmpegService();
        var defaults = OperatingSystem.IsWindows()
            ? new IMediaEngine[] { new WindowsMediaEngine(), new FFmpegMediaEngine(ffmpeg) }
            : new IMediaEngine[] { new FFmpegMediaEngine(ffmpeg) };
        var inspectors = OperatingSystem.IsWindows()
            ? new IMediaInspector[] { new WindowsMediaInspector(), new FFmpegMediaInspector(ffmpeg) }
            : new IMediaInspector[] { new FFmpegMediaInspector(ffmpeg) };
        return new MediaPack(_engine ?? host.GetService<CompositeMediaEngine>() ?? new CompositeMediaEngine(defaults),
            host.Files, host.GetService<IMediaInspector>() ?? new CompositeMediaInspector(inspectors));
    }
}
