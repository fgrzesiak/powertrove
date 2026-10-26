using ContextBar.Application.Packs;
using ContextBar.Application.Resources;
using ContextBar.Core;
using ContextBar.Core.Catalog;

namespace ContextBar.Application.ViewModels;

public sealed record PackConsentDetails(string Id, string Version, string Name, string Publisher, long DownloadBytes);
public enum PackOnDemandOutcome { Ready, Declined, Canceled, Expired, SelectionChanged, Failed, Busy }
public sealed record PackOnDemandResult(PackOnDemandOutcome Outcome, IAction? Action = null, string? Message = null);

/// <summary>Consent/install/re-resolution only. It never executes an action or captures a new selection.</summary>
public sealed class PackOnDemandController(IPackManagementService service, IActionRegistry registry, TimeProvider? clock = null) : IDisposable
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private CancellationTokenSource? _active;
    private bool _closed;
    public bool IsPending { get; private set; }

    public async Task<PackOnDemandResult> ResolveAsync(CatalogAction missing, ContextSnapshot snapshot,
        Func<PackConsentDetails, CancellationToken, Task<bool>> consent,
        Func<bool> stillCurrent,
        Func<PackInstallOffer, CancellationToken, Task<PackChannelStatus>>? install = null,
        CancellationToken token = default)
    {
        if (_closed || IsPending) return new(PackOnDemandOutcome.Busy);
        IsPending = true;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        _active = lifetime;
        bool Expired() => snapshot.ExpiresAt <= _clock.GetUtcNow();
        PackOnDemandResult InvalidContext() => new(Expired() ? PackOnDemandOutcome.Expired : PackOnDemandOutcome.SelectionChanged,
            Message: Strings.GetString("PackInvalidSelection"));
        try
        {
            lifetime.Token.ThrowIfCancellationRequested();
            if (Expired() || !stillCurrent()) return InvalidContext();
            if (snapshot.Status != ContextStatus.Available || snapshot.Content is not FileSelection)
                return Failure(PackChannelStatus.Incompatible);
            if (registry.GetAction(missing.Descriptor.Id) is { } installed && installed is not CatalogAction)
            {
                if (!ActionContextCompatibility.Check(snapshot, installed.Descriptor).IsAvailable) return Failure(PackChannelStatus.Incompatible);
                var available = await installed.MatchAsync(snapshot, lifetime.Token);
                if (Expired() || !stillCurrent()) return InvalidContext();
                return available.IsAvailable ? new(PackOnDemandOutcome.Ready, installed) : Failure(PackChannelStatus.Incompatible);
            }
            var initial = new PackConsentDetails(missing.PackId, missing.PackVersion, missing.PackName, missing.Publisher, missing.DownloadBytes);
            // A decline does not call a service, request metadata or change the captured context.
            if (!await consent(initial, lifetime.Token)) return new(PackOnDemandOutcome.Declined);
            lifetime.Token.ThrowIfCancellationRequested();
            if (Expired() || !stillCurrent()) return InvalidContext();
            var prepared = await service.PrepareAsync(missing.PackId, lifetime.Token);
            if (Expired() || !stillCurrent()) return InvalidContext();
            if (prepared.Status != PackChannelStatus.Ready || prepared.Offer is not { } offer) return Failure(prepared.Status);
            if (offer.Id != missing.PackId) return Failure(PackChannelStatus.InvalidData);
            if (offer.Version != initial.Version || offer.Publisher != initial.Publisher || offer.DownloadBytes != initial.DownloadBytes)
            {
                // A newer catalog cannot silently enlarge or change the download the user approved.
                if (!await consent(new(offer.Id, offer.Version, offer.Name, offer.Publisher, offer.DownloadBytes), lifetime.Token))
                    return new(PackOnDemandOutcome.Declined);
            }
            lifetime.Token.ThrowIfCancellationRequested();
            if (Expired() || !stillCurrent()) return InvalidContext();
            var status = install is null ? await service.InstallAsync(offer, null, lifetime.Token) : await install(offer, lifetime.Token);
            if (Expired() || !stillCurrent()) return InvalidContext();
            if (status != PackChannelStatus.Installed) return Failure(status);
            var action = registry.GetAction(missing.Descriptor.Id);
            if (action is null or CatalogAction || action.Descriptor.Id != missing.Descriptor.Id || !ActionContextCompatibility.Check(snapshot, action.Descriptor).IsAvailable)
                return Failure(PackChannelStatus.InvalidData);
            var match = await action.MatchAsync(snapshot, lifetime.Token);
            if (Expired() || !stillCurrent()) return InvalidContext();
            return match.IsAvailable ? new(PackOnDemandOutcome.Ready, action) : Failure(PackChannelStatus.Incompatible);
        }
        catch (OperationCanceledException) { return new(PackOnDemandOutcome.Canceled); }
        catch (Exception) { return Failure(PackChannelStatus.Unavailable); }
        finally { _active = null; IsPending = false; }
    }
    private static PackOnDemandResult Failure(PackChannelStatus status) => new(PackOnDemandOutcome.Failed, Message: Strings.GetString(status switch
    {
        PackChannelStatus.NotConfigured => "PacksNotConfigured", PackChannelStatus.Offline => "PacksOffline",
        PackChannelStatus.TimedOut => "PacksTimedOut", PackChannelStatus.InvalidData => "PacksInvalidData",
        PackChannelStatus.Incompatible => "PacksIncompatible", PackChannelStatus.Busy => "PacksBusy", _ => "PacksOperationFailed"
    }));
    public void Dispose() { _closed = true; try { _active?.Cancel(); } catch (ObjectDisposedException) { } }
}
