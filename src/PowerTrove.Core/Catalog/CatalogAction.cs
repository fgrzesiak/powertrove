using System.Collections.Frozen;
using PowerTrove.Abstractions.Files;
using PowerTrove.Core.Resources;

namespace PowerTrove.Core.Catalog;

/// <summary>A metadata-only search entry. It has no engine, installer or file access.</summary>
public sealed class CatalogAction : IAction, IFileActionMetadata
{
    public PackInstallState InstallState { get; }
    public string? ManifestFingerprint { get; }
    public string PackId { get; }
    public string PackName => _packName;
    public string PackVersion { get; }
    public string Publisher { get; }
    public long DownloadBytes => _downloadBytes;
    private readonly string _packName;
    private readonly long _downloadBytes;
    private readonly FrozenSet<string>? _extensions;
    public ActionDescriptor Descriptor { get; }
    public IReadOnlyCollection<string>? InputExtensions => _extensions;
    public string DownloadReason => _downloadBytes == 0
        ? Strings.CatalogDownloadUnknown(_packName)
        : Strings.CatalogDownload(_packName, Math.Max(1, Math.Ceiling(_downloadBytes / 1048576d)));

    public CatalogAction(CatalogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.State is not (PackInstallState.NotInstalled or PackInstallState.UpdateAvailable or PackInstallState.Incompatible) || entry.DownloadBytes < 0)
            throw new ArgumentException("Catalog actions require an uninstalled pack.", nameof(entry));
        var action = entry.Action;
        InstallState = entry.State; ManifestFingerprint = entry.ManifestFingerprint;
        PackId = entry.Pack.Id;
        PackVersion = entry.Pack.Version;
        Publisher = entry.Pack.Publisher;
        _packName = entry.Pack.DisplayName;
        _downloadBytes = entry.DownloadBytes;
        _extensions = action.Extensions?.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        var selection = action.Selection?.ToRequirements();
        if (selection is not null)
            selection = selection with
            {
                Families = selection.Families.ToFrozenSet(StringComparer.OrdinalIgnoreCase),
                FamilyLimits = selection.FamilyLimits?.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase)
            };
        Descriptor = new ActionDescriptor(action.Id, action.Title, action.Description ?? string.Empty,
            action.InputKinds.Select(k => k switch
            {
                "none" => ContentKind.None, "files" => ContentKind.Files, "table" => ContentKind.Table,
                "text" => ContentKind.Text, _ => throw new ArgumentException("Invalid catalog input kind.", nameof(entry))
            }).ToFrozenSet(),
            entry.EnglishTitle is { } english && english != action.Title ? new[] { entry.Pack.DisplayName, english } : new[] { entry.Pack.DisplayName },
            RequiresPreview: true, ChangesFiles: action.ChangesFiles ?? true,
            RequiredCapabilities: action.RequiredCapabilities?.ToFrozenSet(StringComparer.Ordinal),
            Selection: selection, CanRememberParameters: false);
        // Parameter fields are deliberately absent: until installed, the only operation
        // is the blocked preview explaining which pack is needed.
    }

    public ValueTask<ActionAvailability> MatchAsync(ContextSnapshot context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var selection = MatchSelection(context);
        return new(!selection.IsAvailable ? selection : InstallState == PackInstallState.Incompatible
            ? new ActionAvailability(false, Strings.CatalogSelectionUnsupported) : new ActionAvailability(true, DownloadReason));
    }

    /// <summary>Local metadata matching for a truthful disabled compatibility hint. Never authorizes execution.</summary>
    public ActionAvailability MatchSelection(ContextSnapshot context)
    {
        var check = ActionContextCompatibility.Check(context, Descriptor);
        if (!check.IsAvailable) return check;
        if (context.Content is FileSelection files)
        {
            var eligible = files.Files.Where(f => _extensions is null || !f.IsDirectory && _extensions.Contains(f.Extension)).ToArray();
            if (eligible.Length == 0 || Descriptor.Selection is { AllowOtherItems: false } && eligible.Length != files.Files.Count)
                return new(false, Strings.CatalogSelectionUnsupported);
            if (Descriptor.Selection is { } selection && SelectionRequirements.Evaluate(selection, context with { Content = new FileSelection(eligible) }) == SelectionFit.None)
                return new(false, Strings.CatalogSelectionUnsupported);
        }
        return new(true);
    }

    public ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters parameters, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new(new ActionPreview(false, DownloadReason, Array.Empty<string>(), Array.Empty<string>(), IsBlocked: true));
    }

    public Task<ActionReceipt> ExecuteAsync(ContextSnapshot context, ActionParameters parameters,
        IProgress<ActionProgress>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ActionReceipt(false, false, Array.Empty<string>(),
            new[] { new ActionFailure(null, DownloadReason) }, false, DownloadReason));
    }
}
