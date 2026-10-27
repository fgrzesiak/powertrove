using PowerTrove.Pack.Images.Windows;
using PowerTrove.Pack.Images.Magick;
using PowerTrove.Abstractions.Ocr;
using PowerTrove.Abstractions.Packs;

[assembly: ActionPackFactory(typeof(PowerTrove.Pack.Images.ImagesPackFactory))]

namespace PowerTrove.Pack.Images;

public sealed record ImagesPackOptions(IReadOnlyList<ImageExportProfile>? Presets = null, ImageLimits? Limits = null);

public sealed class ImagesPackFactory : IPackFactory
{
    private readonly CompositeImageEngine? _engine;
    private readonly ImagesPackOptions? _options;
    public ImagesPackFactory() { }
    public ImagesPackFactory(CompositeImageEngine engine, ImagesPackOptions? options = null)
    { _engine = engine ?? throw new ArgumentNullException(nameof(engine)); _options = options; }
    public ActionPackInfo Info => ImagesPack.Describe();
    public IActionPack Create(IPackHostServices host)
    {
        ArgumentNullException.ThrowIfNull(host);
        var options = _options ?? host.GetService<ImagesPackOptions>();
        bool windows = OperatingSystem.IsWindows();
        var engine = _engine ?? host.GetService<CompositeImageEngine>() ?? new CompositeImageEngine(windows
            ? new IImageEngine[] { new WindowsImagingEngine(), new MagickImageEngine() }
            : new IImageEngine[] { new MagickImageEngine() });
        return new ImagesPack(engine,
            host.Files, options?.Presets, options?.Limits, host.GetService<IImageRasterCodec>() ?? (windows ? new CompositeRasterCodec(new WindowsRasterCodec(), engine) : null),
            host.GetService<IImageLosslessTransformer>() ?? new CoefficientJpegTransformer(), host.GetService<IImageWatermarker>() ?? new MagickImageWatermarker(), host.GetService<IAnimatedGifEncoder>() ?? (windows ? new WindowsAnimatedGifEncoder() : null),
            host.GetService<ITextRecognizer>(), host.GetService<IFaceDetector>() ?? (windows ? new WindowsFaceDetector() : null),
            host.GetService<ISvgRenderer>() ?? new MagickSvgRenderer());
    }
}
