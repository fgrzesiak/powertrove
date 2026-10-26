namespace PowerTrove.Pack.Images;

/// <param name="SourceFormat">Detected <see cref="FileFormats"/> id of the input.</param>
/// <param name="TargetFormat"><see cref="FileFormats"/> id to encode.</param>
/// <param name="Quality">1..100 for lossy formats; 0 keeps the source quality where the engine can determine it.</param>
/// <param name="MaxWidth">Bounding box width; null keeps the size.</param>
/// <param name="MaxHeight">Bounding box height; null keeps the size.</param>
/// <param name="AllowUpscale">Whether images smaller than the box are enlarged.</param>
/// <param name="StripMetadata">Remove EXIF/IPTC/XMP/comments. When false, the engine must preserve metadata.</param>
public sealed record ImageTransformRequest(
    string SourceFormat,
    string TargetFormat,
    int Quality,
    int? MaxWidth,
    int? MaxHeight,
    bool AllowUpscale,
    bool StripMetadata,
    bool RemoveGpsOnly = false);

/// <summary>
/// Port for image codecs. Implementations: Windows Imaging Component (OS-provided)
/// and Magick.NET (prototype dependency per ADR 0005/0007). Engines always apply
/// EXIF orientation to pixels so outputs display upright without metadata.
/// </summary>
public interface IImageEngine
{
    string Name { get; }

    bool CanDecode(string sourceFormat);

    bool CanEncode(string targetFormat);

    /// <summary>Whether the engine can keep metadata when <see cref="ImageTransformRequest.StripMetadata"/> is false.</summary>
    bool CanPreserveMetadata { get; }
    bool CanRemoveGpsOnly => false;

    Task TransformAsync(Stream source, Stream destination, ImageTransformRequest request, CancellationToken cancellationToken);
}

/// <summary>Chooses the first engine able to handle a request (order = preference).</summary>
public sealed class CompositeImageEngine : IImageEngine
{
    private readonly IReadOnlyList<IImageEngine> _engines;

    public CompositeImageEngine(IEnumerable<IImageEngine> engines)
    {
        _engines = engines?.ToList() ?? throw new ArgumentNullException(nameof(engines));
    }

    public string Name => string.Join(", ", _engines.Select(e => e.Name));

    public bool CanDecode(string sourceFormat) => _engines.Any(e => e.CanDecode(sourceFormat));

    public bool CanEncode(string targetFormat) => _engines.Any(e => e.CanEncode(targetFormat));

    public bool CanPreserveMetadata => _engines.Any(e => e.CanPreserveMetadata);
    public bool CanRemoveGpsOnly => _engines.Any(e => e.CanRemoveGpsOnly);

    public IImageEngine? Select(ImageTransformRequest request) =>
        _engines.FirstOrDefault(e => e.CanDecode(request.SourceFormat) && e.CanEncode(request.TargetFormat) && (request.StripMetadata || e.CanPreserveMetadata)
            && (!request.RemoveGpsOnly || !request.StripMetadata && e.CanRemoveGpsOnly));

    public Task TransformAsync(Stream source, Stream destination, ImageTransformRequest request, CancellationToken cancellationToken)
    {
        var engine = Select(request) ?? throw new FileProcessingException("No installed image codec supports this conversion.");
        return engine.TransformAsync(source, destination, request, cancellationToken);
    }
}
