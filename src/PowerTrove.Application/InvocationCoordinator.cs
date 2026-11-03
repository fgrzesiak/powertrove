using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using PowerTrove.Core;
using PowerTrove.Core.Search;
using PowerTrove.Application.Configuration;
using PowerTrove.Application.ViewModels;

namespace PowerTrove.Application;

public class InvocationCoordinator
{
    private readonly IForegroundWindowCapture _foregroundCapture;
    private readonly IContextResolver _contextResolver;
    private readonly PaletteViewModel _paletteViewModel;
    private readonly IActionRegistry _actionRegistry;
    private readonly IActionSearchEngine _actionSearchEngine;
    private readonly ISettingsService? _settingsService;
    private readonly IForegroundTextSelectionCapture? _foregroundText;

    private int _invocationVersion;
    private CancellationTokenSource? _currentInvocationCts;

    public event EventHandler<nint?>? PaletteRequested;
    public event EventHandler? ResurfaceRequested;

    public InvocationCoordinator(
        ISettingsService? settingsService,
        IForegroundWindowCapture foregroundCapture,
        IContextResolver contextResolver,
        PaletteViewModel paletteViewModel,
        IActionRegistry actionRegistry,
        IActionSearchEngine actionSearchEngine,
        IForegroundTextSelectionCapture? foregroundText = null)
    {
        _foregroundCapture = foregroundCapture ?? throw new ArgumentNullException(nameof(foregroundCapture));
        _contextResolver = contextResolver ?? throw new ArgumentNullException(nameof(contextResolver));
        _paletteViewModel = paletteViewModel ?? throw new ArgumentNullException(nameof(paletteViewModel));
        _actionRegistry = actionRegistry ?? throw new ArgumentNullException(nameof(actionRegistry));
        _actionSearchEngine = actionSearchEngine ?? throw new ArgumentNullException(nameof(actionSearchEngine));
        _settingsService = settingsService;
        _foregroundText = foregroundText;
    }

    public async Task InvokeAsync()
    {
        if (_paletteViewModel.IsExecuting)
        {
            ResurfaceRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        var startTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        var version = Interlocked.Increment(ref _invocationVersion);

        _currentInvocationCts?.Cancel();
        _currentInvocationCts?.Dispose();
        _currentInvocationCts = new CancellationTokenSource();
        var token = _currentInvocationCts.Token;

        ForegroundCaptureResult captureResult;

        try
        {
            captureResult = await _foregroundCapture.CaptureForegroundAsync(token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (version != _invocationVersion || token.IsCancellationRequested) return;

        // Browser extension context must be captured while the original window is
        // still focused, before the palette appears. Bound the extra latency.
        ContextSnapshot? browserSnapshot = null;
        bool foregroundBrowser = captureResult.Identity is { ProcessName: { } process } &&
            (process.Equals("chrome", StringComparison.OrdinalIgnoreCase) ||
             process.Equals("msedge", StringComparison.OrdinalIgnoreCase));
        if (foregroundBrowser && captureResult.Identity is { } browserSource)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromMilliseconds(550));
            try
            {
                browserSnapshot = await _contextResolver.ResolveContextAsync(
                    Guid.NewGuid(), browserSource, deadline.Token)
                    .AsTask().WaitAsync(deadline.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
            catch (Exception) when (!token.IsCancellationRequested) { }
        }
        if (version != _invocationVersion || token.IsCancellationRequested) return;

        // UI Automation focus is lost when the palette appears. Query it first, with
        // a strict deadline; a stuck accessibility provider must not block the launcher.
        string? selectedText = null;
        if (captureResult.Identity is { } source && _foregroundText is not null &&
            (browserSnapshot?.Status != ContextStatus.Available) &&
            ForegroundTextSelectionPolicy.ShouldCapture(source))
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(ForegroundTextSelectionPolicy.MaximumWait);
            try
            {
                var selection = await _foregroundText.CaptureAsync(source, deadline.Token)
                    .AsTask().WaitAsync(deadline.Token).ConfigureAwait(true);
                if (ForegroundTextSelectionPolicy.IsValid(selection)) selectedText = selection;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { /* Probe timed out. */ }
            catch (Exception) when (!token.IsCancellationRequested) { /* Unsupported UIA source. */ }
        }
        if (version != _invocationVersion || token.IsCancellationRequested) return;

        _paletteViewModel.Clear();
        PaletteRequested?.Invoke(this, captureResult.Identity?.WindowHandle ?? IntPtr.Zero);
        if (browserSnapshot is { Status: ContextStatus.Available })
        {
            _paletteViewModel.Initialize(browserSnapshot, _actionRegistry, _actionSearchEngine, _settingsService);
            return;
        }

        if (captureResult.Identity == null)
        {
            var noIdentitySnapshot = new ContextSnapshot(
                Guid.NewGuid(),
                new SourceWindowIdentity(IntPtr.Zero, 0, null, DateTimeOffset.UtcNow),
                ContextStatus.Failed,
                new NoContent(),
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddMinutes(5),
                "coordinator",
                PowerTrove.Application.Resources.Strings.GetString("Context_CaptureFailed"));

            _paletteViewModel.Initialize(noIdentitySnapshot, _actionRegistry, _actionSearchEngine, _settingsService);
            return;
        }

        var loadingSnapshot = new ContextSnapshot(
            Guid.NewGuid(),
            captureResult.Identity,
            ContextStatus.Empty,
            new NoContent(),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(5),
            "coordinator",
            PowerTrove.Application.Resources.Strings.GetString("Context_Loading"));

        if (selectedText is not null)
        {
            // Copy only this selection into the invocation. No clipboard mutation or polling.
            var now = DateTimeOffset.UtcNow;
            var textSnapshot = new ContextSnapshot(loadingSnapshot.InvocationId,
                captureResult.Identity, ContextStatus.Available, new TextSelection(selectedText),
                now, now.AddMinutes(2), "powertrove.provider.windows.uia-text", null);
            _paletteViewModel.Initialize(textSnapshot, _actionRegistry, _actionSearchEngine, _settingsService);
            PowerTrove.Core.Diagnostics.InvocationMetrics.InvocationLatency.Record(
                System.Diagnostics.Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds);
            return;
        }

        if (browserSnapshot is not null)
        {
            // No live/unique browser context, and UIA found no selection either.
            // Avoid a second extension request after the palette has taken focus.
            _paletteViewModel.Initialize(browserSnapshot, _actionRegistry, _actionSearchEngine, _settingsService);
            return;
        }
        _paletteViewModel.Initialize(loadingSnapshot, _actionRegistry, _actionSearchEngine, _settingsService);

        try
        {
            var resolveStart = System.Diagnostics.Stopwatch.GetTimestamp();
            var resolvedSnapshot = await _contextResolver.ResolveContextAsync(
                loadingSnapshot.InvocationId,
                captureResult.Identity,
                token).ConfigureAwait(true);
            PowerTrove.Core.Diagnostics.InvocationMetrics.ContextResolveLatency.Record(
                System.Diagnostics.Stopwatch.GetElapsedTime(resolveStart).TotalMilliseconds,
                new KeyValuePair<string, object?>(PowerTrove.Core.Diagnostics.InvocationMetrics.ProviderTag, resolvedSnapshot.ProviderId));

            if (version == _invocationVersion && !token.IsCancellationRequested)
            {
                _paletteViewModel.Initialize(resolvedSnapshot, _actionRegistry, _actionSearchEngine, _settingsService);
            }
        }
        catch (OperationCanceledException)
        {
            // Ignored, superceded.
        }
        catch (Exception)
        {
            if (version == _invocationVersion && !token.IsCancellationRequested)
            {
                var errorSnapshot = new ContextSnapshot(
                    loadingSnapshot.InvocationId,
                    captureResult.Identity,
                    ContextStatus.Failed,
                    new NoContent(),
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow.AddMinutes(5),
                    "coordinator",
                    PowerTrove.Application.Resources.Strings.GetString("Context_ResolutionFailed"));
                _paletteViewModel.Initialize(errorSnapshot, _actionRegistry, _actionSearchEngine, _settingsService);
            }
        }
        finally
        {
            var elapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            PowerTrove.Core.Diagnostics.InvocationMetrics.InvocationLatency.Record(elapsedMs);
        }
    }

    /// <summary>Explicit activation bypasses foreground capture/resolution and only prepares the normal action UI.</summary>
    public async Task<bool> ActivateSnapshotAsync(ContextSnapshot snapshot, string? actionId = null)
    {
        if (_paletteViewModel.IsExecuting)
        {
            ResurfaceRequested?.Invoke(this, EventArgs.Empty);
            return false;
        }
        var version = Interlocked.Increment(ref _invocationVersion);
        _currentInvocationCts?.Cancel();
        _currentInvocationCts?.Dispose();
        _currentInvocationCts = null;
        _paletteViewModel.Clear();
        _paletteViewModel.Initialize(snapshot, _actionRegistry, _actionSearchEngine, _settingsService);
        PaletteRequested?.Invoke(this, IntPtr.Zero);
        await _paletteViewModel.SearchTask.ConfigureAwait(true);
        if (version != _invocationVersion) return false;
        if (actionId is not null)
        {
            var requested = _paletteViewModel.FilteredActions.FirstOrDefault(result => result.IsCompatible
                && result.Action.Descriptor.Id == actionId && result.Action is not PowerTrove.Core.Catalog.CatalogAction);
            if (requested is not null) _paletteViewModel.SelectedAction = requested;
        }
        return true;
    }

    public void Dismiss()
    {
        Interlocked.Increment(ref _invocationVersion);
        _currentInvocationCts?.Cancel();
        _paletteViewModel.Clear();
    }
}
