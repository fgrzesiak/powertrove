using System.Collections.Frozen;
using ContextBar.Abstractions.Manifest;
using ContextBar.Core;
using ContextBar.Core.Catalog;

namespace ContextBar.Application.Packs;

/// <summary>Registers verified installed metadata as remote actions without loading pack assemblies in Desktop.</summary>
public sealed class InstalledPackRuntime(PackInstaller installer, PackHostSupervisor supervisor,
    ActionRegistry registry, IReadOnlyList<IActionPack> builtIns, IActionCatalog catalog)
{
    private readonly HashSet<string> _registered = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    public async Task ReloadAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var packs = await installer.GetInstalledAsync(token).ConfigureAwait(false);
            var current = packs.Select(p => p.Manifest.Id).ToHashSet(StringComparer.Ordinal);
            foreach (string id in _registered.Where(id => !current.Contains(id)).ToArray())
            {
                var builtIn = builtIns.SingleOrDefault(p => p.Info.Id == id);
                registry.RegisterPack(id, builtIn?.Actions ?? catalog.Entries.Where(e => e.Pack.Id == id)
                    .Select(e => (IAction)new CatalogAction(e with { State = PackInstallState.NotInstalled })).ToArray());
                _registered.Remove(id);
            }
            foreach (var pack in packs)
            {
                var group = new PackWorkerGroup(pack.WorkerGroupId, [pack.Directory], [new(pack.Manifest.Id, pack.Manifest.Version)], [pack.ManifestSha256]);
                var actions = pack.Manifest.Actions.Select(a => (IAction)new RemoteAction(supervisor, group, Describe(a, pack.Manifest.DisplayName),
                    a.Extensions, ct => installer.ConfirmStartedAsync(pack.Manifest.Id, pack.Manifest.Version, ct))).ToArray();
                registry.RegisterPack(pack.Manifest.Id, actions);
                _registered.Add(pack.Manifest.Id);
            }
        }
        finally { _gate.Release(); }
    }
    private static ActionDescriptor Describe(ManifestAction action, string packName) => new(action.Id, action.Title,
        action.Description ?? string.Empty, new[] { ContentKind.Files }.ToFrozenSet(), [packName],
        action.RequiresPreview ?? true, action.ChangesFiles ?? true, action.RequiredCapabilities?.ToFrozenSet(StringComparer.Ordinal),
        Parameters: action.Parameters?.Select(Parameter).ToArray(), Selection: action.Selection?.ToRequirements(), CanRememberParameters: false);
    private static ActionParameterDefinition Parameter(ManifestParameter parameter) => new(parameter.Name, parameter.Type switch
    {
        "boolean" => ActionParameterKind.Boolean, "number" => ActionParameterKind.Integer, "choice" => ActionParameterKind.Choice,
        "password" => ActionParameterKind.Password, "file" => ActionParameterKind.FilePath, "color" => ActionParameterKind.Color,
        _ => ActionParameterKind.Text
    }, parameter.Title ?? parameter.Name, parameter.Description, parameter.Type == "password" ? null : parameter.Default,
        Choices: parameter.Choices?.Select(c => new ActionParameterChoice(c, c)).ToArray());
}
