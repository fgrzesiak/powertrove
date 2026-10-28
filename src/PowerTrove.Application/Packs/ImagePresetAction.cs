using System.Collections.Frozen;
using System.Globalization;
using PowerTrove.Abstractions.Files;
using PowerTrove.Application.Configuration;
using PowerTrove.Core.Catalog;

namespace PowerTrove.Application.Packs;

/// <summary>
/// A saved image export preset. The host owns it and runs the matching conversion of whichever
/// Images pack is installed, in process or in a worker, with the preset's fixed values.
/// </summary>
public sealed class ImagePresetAction : IAction, IFileActionMetadata
{
    private readonly SavedImageExportPreset _preset;
    private readonly Func<string, IAction?> _resolve;
    private readonly string _targetId;

    public ImagePresetAction(SavedImageExportPreset preset, Func<string, IAction?> resolve)
    {
        _preset = preset ?? throw new ArgumentNullException(nameof(preset));
        _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
        string format = preset.Format?.Trim().ToLowerInvariant() switch
        {
            "avif" => "avif", "jpeg" or "jpg" => "jpeg", "png" => "png", _ => "webp"
        };
        _targetId = "powertrove.image.convert-" + format;
        Descriptor = new ActionDescriptor(preset.Id, preset.Title, preset.Description,
            new[] { ContentKind.Files }.ToFrozenSet(), ["preset", "image", "export", format], RequiresPreview: true, ChangesFiles: true,
            Selection: SelectionRequirements.Any("image"));
    }

    public ActionDescriptor Descriptor { get; }
    public IReadOnlyCollection<string>? InputExtensions => (Target() as IFileActionMetadata)?.InputExtensions;

    /// <summary>Parameters of the Images pack's convert actions; width and height are set together or not at all.</summary>
    public DictionaryParameters Parameters()
    {
        bool sized = _preset.MaxWidth is > 0 && _preset.MaxHeight is > 0;
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["StripMetadata"] = _preset.StripMetadata ? "true" : "false",
            ["MaxWidth"] = (sized ? _preset.MaxWidth!.Value : 0).ToString(CultureInfo.InvariantCulture),
            ["MaxHeight"] = (sized ? _preset.MaxHeight!.Value : 0).ToString(CultureInfo.InvariantCulture)
        };
        if (_targetId != "powertrove.image.convert-png")
            values["Quality"] = Math.Clamp(_preset.Quality, 1, 100).ToString(CultureInfo.InvariantCulture);
        return new DictionaryParameters(values);
    }

    // A catalog placeholder means the Images pack is not installed.
    private IAction? Target() => _resolve(_targetId) is { } action and not CatalogAction ? action : null;

    public ValueTask<ActionAvailability> MatchAsync(ContextSnapshot context, CancellationToken cancellationToken) =>
        Target() is { } target ? target.MatchAsync(context, cancellationToken)
            : new(new ActionAvailability(false, PackStrings.ImagePresetNeedsImages));

    public ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters parameters, CancellationToken cancellationToken) =>
        Target() is { } target ? target.PreviewAsync(context, Parameters(), cancellationToken)
            : new(new ActionPreview(false, PackStrings.ImagePresetNeedsImages, [], [], IsBlocked: true));

    public Task<ActionReceipt> ExecuteAsync(ContextSnapshot context, ActionParameters parameters, IProgress<ActionProgress>? progress,
        CancellationToken cancellationToken) =>
        Target() is { } target ? target.ExecuteAsync(context, Parameters(), progress, cancellationToken)
            : Task.FromResult(new ActionReceipt(false, false, [], [new ActionFailure(null, PackStrings.ImagePresetNeedsImages)], false));
}
