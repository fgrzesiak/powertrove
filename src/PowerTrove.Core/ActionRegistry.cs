using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Linq;
using PowerTrove.Core.Catalog;

namespace PowerTrove.Core;

public class ActionRegistry : IActionRegistry, IActionRegistryChanges
{
    public event EventHandler? Changed;
    public long Revision => Interlocked.Read(ref _revision);
    private void PublishChange()
    {
        Interlocked.Increment(ref _revision);
        if (Changed is not { } handlers) return;
        foreach (EventHandler handler in handlers.GetInvocationList())
            try { handler(this, EventArgs.Empty); }
            catch (Exception) { /* Registry state is already committed; isolate UI observers. */ }
    }
    private long _revision;

    private readonly ConcurrentDictionary<string, IAction> _actions = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _registrationGate = new();
    private readonly Dictionary<string, string> _packOwners = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Replaces one pack atomically; IDs owned by other packs or app commands cannot be replaced.</summary>
    public void RegisterPack(string packId, IReadOnlyList<IAction> actions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packId);
        ArgumentNullException.ThrowIfNull(actions);
        lock (_registrationGate)
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var action in actions)
            {
                if (action is null || !ids.Add(action.Descriptor.Id) || action is CatalogAction catalog && catalog.PackId != packId)
                    throw new InvalidOperationException("Invalid pack action inventory.");
                if (_actions.TryGetValue(action.Descriptor.Id, out var existing)
                    && !(_packOwners.TryGetValue(action.Descriptor.Id, out var owner) && owner == packId)
                    && !(existing is CatalogAction missing && missing.PackId == packId))
                    throw new InvalidOperationException("An action ID belongs to another pack or app command.");
            }
            var previous = _actions.Where(pair => _packOwners.TryGetValue(pair.Key, out var owner) && owner == packId
                || pair.Value is CatalogAction catalog && catalog.PackId == packId).Select(pair => pair.Key).ToArray();
            foreach (string id in previous) { _actions.TryRemove(id, out _); _packOwners.Remove(id); }
            foreach (var action in actions) { _actions[action.Descriptor.Id] = action; _packOwners[action.Descriptor.Id] = packId; }
        }
        PublishChange();
    }

    public void Register(IAction action)
    {
        if (action == null) throw new ArgumentNullException(nameof(action));
        lock (_registrationGate)
        {
            if (_actions.TryGetValue(action.Descriptor.Id, out var existing) &&
                (existing is CatalogAction || action is CatalogAction))
                throw new InvalidOperationException("Catalog and installed action IDs must be unique.");
            _actions[action.Descriptor.Id] = action;
            _packOwners.Remove(action.Descriptor.Id);
        }
        PublishChange();
    }

    /// <summary>Adds missing packs atomically; an invalid catalog never replaces installed actions.</summary>
    public void RegisterCatalog(IActionCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var actions = catalog.Entries.Where(e => e.State == PackInstallState.NotInstalled)
            .Select(e => new CatalogAction(e)).ToArray();
        lock (_registrationGate)
        {
            var ids = new HashSet<string>(_actions.Keys, StringComparer.OrdinalIgnoreCase);
            foreach (var action in actions)
                if (!ids.Add(action.Descriptor.Id))
                    throw new InvalidOperationException("Catalog and installed action IDs must be unique.");
            foreach (var action in actions) _actions[action.Descriptor.Id] = action;
        }
        PublishChange();
    }

    /// <summary>Refreshes discovery without replacing any installed action or advertising a newer descriptor as installed.</summary>
    public void ReplaceCatalog(IActionCatalog catalog)
    {
        var actions = catalog.Entries.Where(e => e.State is PackInstallState.NotInstalled or PackInstallState.UpdateAvailable or PackInstallState.Incompatible)
            .Select(e => new CatalogAction(e)).ToArray();
        if (actions.Select(a => a.Descriptor.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != actions.Length)
            throw new InvalidOperationException("Duplicate discovery action.");
        lock (_registrationGate)
        {
            foreach (var id in _actions.Where(p => p.Value is CatalogAction).Select(p => p.Key).ToArray())
            { _actions.TryRemove(id, out _); _packOwners.Remove(id); }
            foreach (var action in actions)
                if (!_actions.ContainsKey(action.Descriptor.Id)) _actions[action.Descriptor.Id] = action;
        }
        PublishChange();
    }

    public IAction? GetAction(string id)
    {
        if (id == null) throw new ArgumentNullException(nameof(id));
        lock (_registrationGate) { _actions.TryGetValue(id, out var action); return action; }
    }

    public IEnumerable<IAction> GetAllActions()
    {
        lock (_registrationGate) return _actions.Values.ToArray();
    }

    public async IAsyncEnumerable<IAction> GetAvailableActionsAsync(
        ContextSnapshot context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (context == null) throw new ArgumentNullException(nameof(context));

        if (context.Status != ContextStatus.Available)
        {
            yield break;
        }

        foreach (var action in GetAllActions())
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Identical static rules in discovery and search. Missing capability IDs
            // fail closed before the action's dynamic matcher executes.
            if (!ActionContextCompatibility.Check(context, action.Descriptor).IsAvailable)
                continue;

            ActionAvailability availability;
            try
            {
                availability = await action.MatchAsync(context, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // A broken action must not hide unrelated actions or leak raw context.
                continue;
            }

            // yield must remain outside try/catch blocks in async iterators.
            if (availability.IsAvailable)
                yield return action;
        }
    }
}
