using System.Collections.Frozen;
using PowerTrove.Abstractions.Manifest;
using PowerTrove.Core;
using PowerTrove.Core.Catalog;

namespace PowerTrove.Application.Packs;

/// <summary>Registers verified installed metadata as remote actions without loading pack assemblies in Desktop.</summary>
public sealed class InstalledPackRuntime
{
    /// <summary>Consecutive start failures before a never-confirmed version is replaced by its predecessor.</summary>
    public const int RollbackAfterStartFailures = 2;
    private readonly PackInstaller installer;
    private readonly PackHostSupervisor supervisor;
    private readonly ActionRegistry registry;
    private readonly IReadOnlyList<IActionPack> builtIns;
    private readonly bool preferBuiltIns;
    private readonly HashSet<string> _registered = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    public IActionCatalog Catalog { get; set; }
    /// <summary>Raised with the pack ID after a failed new version was replaced by its previous version.</summary>
    public event Action<string>? RolledBack;

    public InstalledPackRuntime(PackInstaller installer, PackHostSupervisor supervisor,
        ActionRegistry registry, IReadOnlyList<IActionPack> builtIns, IActionCatalog catalog, bool preferBuiltIns = false)
    {
        this.installer = installer; this.supervisor = supervisor; this.registry = registry; this.builtIns = builtIns; Catalog = catalog;
        this.preferBuiltIns = preferBuiltIns;
        supervisor.StartFailed += (group, failures) =>
        {
            if (failures >= RollbackAfterStartFailures && group.Packs is [var pack]) _ = RollBackAsync(pack.Id, pack.Version);
        };
    }

    /// <summary>Restores the previous version of a pack whose new version never started, then re-registers its actions.</summary>
    public async Task<bool> RollBackAsync(string id, string version, CancellationToken token = default)
    {
        try
        {
            if (!await installer.RollBackAsync(id, version, token).ConfigureAwait(false)) return false;
            await ReloadAsync(token).ConfigureAwait(false);
            RolledBack?.Invoke(id);
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException
            or System.Text.Json.JsonException or ArgumentException) { return false; }
    }
    public async Task ReloadAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var packs = (await installer.GetInstalledAsync(token).ConfigureAwait(false))
                .Where(p => !preferBuiltIns || !builtIns.Any(b => b.Info.Id == p.Manifest.Id)).ToArray();
            // App-owned isolated groups may contain several packs. Renew all their bindings
            // after maintenance before applying any managed overrides.
            foreach (var builtIn in builtIns)
                registry.RegisterPack(builtIn.Info.Id, builtIn.Actions.Select(a => a is RemoteAction remote ? remote.Rebind() : a).ToArray());
            var current = packs.Select(p => p.Manifest.Id).ToHashSet(StringComparer.Ordinal);
            foreach (string id in _registered.Where(id => !current.Contains(id)).ToArray())
            {
                var builtIn = builtIns.SingleOrDefault(p => p.Info.Id == id);
                registry.RegisterPack(id, builtIn?.Actions.Select(a => a is RemoteAction remote ? remote.Rebind() : a).ToArray() ?? Catalog.Entries.Where(e => e.Pack.Id == id && e.State != PackInstallState.Incompatible)
                    .Select(e => (IAction)new CatalogAction(e with { State = PackInstallState.NotInstalled })).ToArray());
                _registered.Remove(id);
            }
            foreach (var pack in packs)
            {
                var group = new PackWorkerGroup(pack.WorkerGroupId, [pack.Directory], [new(pack.Manifest.Id, pack.Manifest.Version)], [pack.ManifestSha256]);
                var presentation = ManifestLocalization.ForCulture(pack.Manifest);
                var englishTitles = pack.Manifest.Actions.ToDictionary(a => a.Id, a => a.Title, StringComparer.Ordinal);
                var actions = presentation.Actions.Select(a => (IAction)new RemoteAction(supervisor, group,
                    Describe(a, presentation.DisplayName, englishTitles[a.Id]),
                    a.Extensions, ct => installer.ConfirmStartedAsync(pack.Manifest.Id, pack.Manifest.Version, ct))).ToArray();
                registry.RegisterPack(pack.Manifest.Id, actions);
                _registered.Add(pack.Manifest.Id);
            }
            registry.ReplaceCatalog(Catalog);
        }
        finally { _gate.Release(); }
    }
    private static ActionDescriptor Describe(ManifestAction action, string packName, string englishTitle) => new(action.Id, action.Title,
        action.Description ?? string.Empty, action.InputKinds.Select(k => k switch { "files" => ContentKind.Files, "table" => ContentKind.Table, "text" => ContentKind.Text, _ => ContentKind.None }).ToFrozenSet(),
        englishTitle == action.Title ? [packName] : [packName, englishTitle],
        action.RequiresPreview ?? true, action.ChangesFiles ?? true, action.RequiredCapabilities?.ToFrozenSet(StringComparer.Ordinal),
        Parameters: action.Parameters?.Select(Parameter).ToArray(), Selection: action.Selection?.ToRequirements(), CanRememberParameters: false);
    private static ActionParameterDefinition Parameter(ManifestParameter parameter) => new(parameter.Name, parameter.Type switch
    {
        "boolean" => ActionParameterKind.Boolean, "number" => ActionParameterKind.Integer, "choice" => ActionParameterKind.Choice,
        "password" => ActionParameterKind.Password, "file" => ActionParameterKind.FilePath, "color" => ActionParameterKind.Color,
        _ => ActionParameterKind.Text
    }, parameter.Title ?? parameter.Name, parameter.Description, parameter.Type == "password" ? null : parameter.Default,
        Minimum: parameter.Minimum, Maximum: parameter.Maximum,
        Choices: parameter.Choices?.Select(c => new ActionParameterChoice(c, parameter.ChoiceTitles?.GetValueOrDefault(c) ?? c)).ToArray(),
        Placeholder: parameter.Placeholder, FileTypes: parameter.FileTypes, MustExist: parameter.MustExist,
        VisibleWhenParameter: parameter.VisibleWhenParameter, VisibleWhenValue: parameter.VisibleWhenValue,
        Editor: parameter.Editor == "multiSelect" ? ActionParameterEditor.MultiSelect : ActionParameterEditor.Default);
}
