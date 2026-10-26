using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ContextBar.Application.Configuration;
using ContextBar.Application.Resources;
using ContextBar.Application.Services;
using ContextBar.Application.Search;
using ContextBar.Core;
using ContextBar.Core.Search;
using ContextBar.Core.Selection;

namespace ContextBar.Application.ViewModels;

public sealed class PaletteViewModel : INotifyPropertyChanged
{
    private string _searchText = string.Empty;
    private string _contextSummary = string.Empty;
    private string _hotkeyWarningMessage = string.Empty;
    private ContextSnapshot? _currentSnapshot;
    private IActionSearchEngine? _searchEngine;
    private IActionRegistry? _actionRegistry;
    private IReadOnlyList<ActionSearchResult> _filteredActions = Array.Empty<ActionSearchResult>();
    private IReadOnlyList<ActionGroup> _groups = Array.Empty<ActionGroup>();
    private ActionSearchResult? _selectedAction;
    private ISettingsService? _settingsService;
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _executionCts;
    private long _searchRevision;
    private IReadOnlyList<ActionSearchResult> _searchResults = Array.Empty<ActionSearchResult>();
    private bool _includeUnavailableActions;
    private bool _isSearching;

    // Published last, after the complete result snapshot is assigned. Hosts rebuild only once.
    public long ResultsRevision { get; private set; }
    /// <summary>Explicit selection changes invalidate Enter requests waiting for search completion.</summary>
    public long SelectionRevision { get; private set; }
    private IReadOnlyList<ActionSearchResult> VisibleSearchResults => _settingsService?.Settings.ShowNotInstalledActions != false
        ? _searchResults : _searchResults.Where(r => r.InstallState != ContextBar.Core.Catalog.PackInstallState.NotInstalled).ToArray();
    public int UnavailableActionCount => VisibleSearchResults.Count(r => !r.IsCompatible);
    public bool HasUnavailableActions => UnavailableActionCount > 0;
    public string ShowUnavailableLabel => string.Format(Strings.GetString(UnavailableActionCount == 1 ? "ShowUnavailable_One" : "ShowUnavailable_Other"), UnavailableActionCount);
    public bool HasNoResults => _currentSnapshot != null && !_isSearching && !IsExecuting && SearchError == null && _filteredActions.Count == 0;

    public bool IncludeUnavailableActions
    {
        get => _includeUnavailableActions;
        set
        {
            if (_includeUnavailableActions == value || IsExecuting) return;
            _includeUnavailableActions = value;
            SelectionRevision++;
            OnPropertyChanged(nameof(IncludeUnavailableActions));
            PublishResults(ActionGrouping.Create(VisibleSearchResults, !string.IsNullOrWhiteSpace(_searchText), value), preserveSelection: true);
        }
    }

    private bool _isExecuting;
    private double _executionProgress;
    private string? _executionStatusMessage;
    private string? _executionResultMessage;
    private bool _hasExecutionResult;
    private bool _executionIsSuccess;
    private bool _showFirstRun;

    private IReadOnlyList<string> _currentOutputPaths = Array.Empty<string>();

    public SelectionSignature? CurrentSelectionSignature { get; private set; }

    public IReadOnlyList<string> CurrentOutputPaths
    {
        get => _currentOutputPaths;
        private set
        {
            if (_currentOutputPaths != value)
            {
                _currentOutputPaths = value;
                OnPropertyChanged(nameof(CurrentOutputPaths));
                OnPropertyChanged(nameof(HasOutputs));
            }
        }
    }

    public bool HasOutputs => _currentOutputPaths != null && _currentOutputPaths.Count > 0;


    // Exposed so tests and embedding hosts can await search completion.
    public Task SearchTask { get; private set; } = Task.CompletedTask;
    public string? SearchError { get; private set; }

    public Task RetrySearchAsync()
    {
        if (IsExecuting) return Task.CompletedTask;
        SearchTask = UpdateFilteredActionsAsync();
        return SearchTask;
    }

    public IUndoService? UndoService { get; set; }
    private Guid? _undoOperationId;
    private DateTimeOffset? _undoUntil;
    private bool _isUndoing;
    public bool CanUndoResult => !_isUndoing && !IsExecuting && _undoOperationId.HasValue && _undoUntil > DateTimeOffset.UtcNow && UndoService is not null;
    public void RefreshUndoAvailability() => OnPropertyChanged(nameof(CanUndoResult));
    private void ClearUndo()
    {
        _undoOperationId = null; _undoUntil = null; RefreshUndoAvailability();
    }
    public async Task UndoResultAsync(CancellationToken cancellationToken = default)
    {
        if (!CanUndoResult || UndoService is null || _undoOperationId is not { } id) return;
        _isUndoing = true; RefreshUndoAvailability();
        try
        {
            var result = await UndoService.UndoAsync(id, cancellationToken);
            if (_undoOperationId != id) return;
            ExecutionResultMessage = Strings.GetString(result.Success ? "RenameUndoCompleted" : "RenameUndoUnavailable");
            ExecutionIsSuccess = result.Success;
            if (result.Success) ClearUndo();
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            ExecutionIsSuccess = false;
            ExecutionResultMessage = Strings.GetString("RenameUndoUnavailable");
        }
        finally { _isUndoing = false; RefreshUndoAvailability(); }
    }

    public IClipboardWriter? ClipboardWriter { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? DismissRequested;
    public IOutputRevealer? OutputRevealer { get; set; }
    public ContextBar.Application.Configuration.ISettingsService? SettingsService { get; set; }

    private int _outputCount;
    public int OutputCount
    {
        get => _outputCount;
        private set
        {
            if (_outputCount != value)
            {
                _outputCount = value;
                OnPropertyChanged(nameof(OutputCount));
            }
        }
    }

    private int _failureCount;
    public int FailureCount
    {
        get => _failureCount;
        private set
        {
            if (_failureCount != value)
            {
                _failureCount = value;
                OnPropertyChanged(nameof(FailureCount));
            }
        }
    }

    public bool CanOpenSingleOutput => OutputCount == 1;

    private long _outputNavigationRevision;
    private string? _outputNavigationMessage;
    public string? OutputNavigationMessage
    {
        get => _outputNavigationMessage;
        private set
        {
            if (_outputNavigationMessage == value) return;
            _outputNavigationMessage = value;
            OnPropertyChanged(nameof(OutputNavigationMessage));
        }
    }

    public async Task RevealOutputsAsync()
    {
        if (!HasExecutionResult || CurrentOutputPaths.Count == 0) return;
        var outputs = CurrentOutputPaths;
        var invocation = _currentSnapshot?.InvocationId;
        long revision = ++_outputNavigationRevision;
        OutputNavigationMessage = null;
        try
        {
            if (OutputRevealer is null) throw new OutputRevealException();
            await OutputRevealer.RevealOutputsAsync(outputs);
        }
        catch (Exception)
        {
            if (revision == _outputNavigationRevision && HasExecutionResult && ReferenceEquals(outputs, CurrentOutputPaths)
                && invocation == _currentSnapshot?.InvocationId)
                OutputNavigationMessage = Strings.GetString("ResultRevealFailed");
        }
    }

    public async Task OpenSingleOutputAsync()
    {
        if (OutputRevealer != null && CurrentOutputPaths.Count == 1)
        {
            await OutputRevealer.OpenSingleOutputAsync(CurrentOutputPaths[0]);
        }
    }

    public void Done()
    {
        DismissRequested?.Invoke(this, EventArgs.Empty);
    }


    public string SearchText
    {
        get => _searchText;
        set
        {
            if (_searchText != value)
            {
                _searchText = value;
                ClearExecutionResultForBrowsing();
                OnPropertyChanged(nameof(SearchText));
                if (!IsExecuting) SearchTask = UpdateFilteredActionsAsync();
            }
        }
    }

    public bool ShowFirstRun
    {
        get => _showFirstRun;
        set
        {
            if (_showFirstRun != value)
            {
                _showFirstRun = value;
                OnPropertyChanged(nameof(ShowFirstRun));
            }
        }
    }

    public void DismissFirstRun()
    {
        ShowFirstRun = false;
    }

    private string? _catalogWarningMessage;
    public string? CatalogWarningMessage
    {
        get => _catalogWarningMessage;
        set
        {
            if (_catalogWarningMessage == value) return;
            _catalogWarningMessage = value;
            OnPropertyChanged(nameof(CatalogWarningMessage));
        }
    }

    public string HotkeyWarningMessage
    {
        get => _hotkeyWarningMessage;
        set
        {
            if (_hotkeyWarningMessage != value)
            {
                _hotkeyWarningMessage = value;
                OnPropertyChanged(nameof(HotkeyWarningMessage));
            }
        }
    }

    private string? _contextNotice;

    /// <summary>Localized explanation shown when the selection could not be used; null otherwise.</summary>
    public string? ContextNotice
    {
        get => _contextNotice;
        private set
        {
            if (_contextNotice != value)
            {
                _contextNotice = value;
                OnPropertyChanged(nameof(ContextNotice));
                OnPropertyChanged(nameof(IsContextAvailable));
            }
        }
    }

    /// <summary>The captured context the palette currently works on (null before the first invocation).</summary>
    public ContextSnapshot? CurrentSnapshot => _currentSnapshot;

    /// <summary>True when the captured selection can be used by actions.</summary>
    public bool IsContextAvailable => _currentSnapshot?.Status == ContextStatus.Available;

    public string ContextSummary
    {
        get => _contextSummary;
        private set
        {
            if (_contextSummary != value)
            {
                _contextSummary = value;
                OnPropertyChanged(nameof(ContextSummary));
            }
        }
    }

    public IReadOnlyList<ActionGroup> Groups => _groups;
    public IReadOnlyList<ActionSearchResult> FilteredActions => _filteredActions;

    public ActionSearchResult? SelectedAction
    {
        get => _selectedAction;
        set
        {
            if (_selectedAction != value)
            {
                _selectedAction = value;
                SelectionRevision++;
                if (!IsExecuting) ClearExecutionResultForBrowsing();
                OnPropertyChanged(nameof(SelectedAction));
            }
        }
    }


    public bool ShowCancelConfirmation
    {
        get => _showCancelConfirmation;
        private set
        {
            if (_showCancelConfirmation != value)
            {
                _showCancelConfirmation = value;
                OnPropertyChanged(nameof(ShowCancelConfirmation));
            }
        }
    }
    private bool _showCancelConfirmation;

    public bool IsExecuting
    {
        get => _isExecuting;
        private set
        {
            if (_isExecuting != value)
            {
                _isExecuting = value;
                OnPropertyChanged(nameof(IsExecuting));
                OnPropertyChanged(nameof(IsInputEnabled));
            }
        }
    }

    public bool IsInputEnabled => !IsExecuting;

    public double ExecutionProgress
    {
        get => _executionProgress;
        private set
        {
            if (_executionProgress != value)
            {
                _executionProgress = value;
                OnPropertyChanged(nameof(ExecutionProgress));
            }
        }
    }

    public string? ExecutionStatusMessage
    {
        get => _executionStatusMessage;
        private set
        {
            if (_executionStatusMessage != value)
            {
                _executionStatusMessage = value;
                OnPropertyChanged(nameof(ExecutionStatusMessage));
            }
        }
    }

    public string? ExecutionResultMessage
    {
        get => _executionResultMessage;
        private set
        {
            if (_executionResultMessage != value)
            {
                _executionResultMessage = value;
                OnPropertyChanged(nameof(ExecutionResultMessage));
            }
        }
    }

    public bool HasExecutionResult
    {
        get => _hasExecutionResult;
        private set
        {
            if (_hasExecutionResult != value)
            {
                _hasExecutionResult = value;
                if (!value)
                {
                    _outputNavigationRevision++;
                    OutputNavigationMessage = null;
                }
                OnPropertyChanged(nameof(HasExecutionResult));
            }
        }
    }

    public bool ExecutionIsSuccess
    {
        get => _executionIsSuccess;
        private set
        {
            if (_executionIsSuccess != value)
            {
                _executionIsSuccess = value;
                OnPropertyChanged(nameof(ExecutionIsSuccess));
            }
        }
    }

    public void MoveSelectionUp()
    {
        if (_filteredActions.Count == 0 || IsExecuting) return;
        if (_selectedAction == null)
        {
            SelectedAction = _filteredActions.Last();
            return;
        }

        int index = _filteredActions.ToList().IndexOf(_selectedAction);
        if (index <= 0)
        {
            SelectedAction = _filteredActions.Last(); // Wrap around
        }
        else
        {
            SelectedAction = _filteredActions[index - 1];
        }
    }

    public void MoveSelectionDown()
    {
        if (_filteredActions.Count == 0 || IsExecuting) return;
        if (_selectedAction == null)
        {
            SelectedAction = _filteredActions.First();
            return;
        }

        int index = _filteredActions.ToList().IndexOf(_selectedAction);
        if (index == -1 || index >= _filteredActions.Count - 1)
        {
            SelectedAction = _filteredActions.First(); // Wrap around
        }
        else
        {
            SelectedAction = _filteredActions[index + 1];
        }
    }

    public void Clear()
    {
        if (IsExecuting) return;

        _searchCts?.Cancel();
        Interlocked.Increment(ref _searchRevision);

        _currentSnapshot = null;
        SelectionRevision++;
        CurrentSelectionSignature = null;
        ContextSummary = Strings.GetString("Context_UnknownSelection");
        ContextNotice = null;
        _searchText = string.Empty;
        _searchResults = Array.Empty<ActionSearchResult>();
        _includeUnavailableActions = false;
        _isSearching = false;
        SearchError = null;
        SearchTask = Task.CompletedTask;
        ClearExecutionResultForBrowsing();
        OnPropertyChanged(nameof(SearchText));
        OnPropertyChanged(nameof(SearchError));
        OnPropertyChanged(nameof(IncludeUnavailableActions));
        OnPropertyChanged(nameof(IsContextAvailable));
        PublishResults(Array.Empty<ActionGroup>());
    }

    public void Initialize(ContextSnapshot snapshot, IActionRegistry actionRegistry, IActionSearchEngine searchEngine, ISettingsService? settingsService = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (_currentSnapshot?.InvocationId != snapshot.InvocationId && !IsExecuting) Clear();
        _currentSnapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        _actionRegistry = actionRegistry ?? throw new ArgumentNullException(nameof(actionRegistry));
        _searchEngine = searchEngine ?? throw new ArgumentNullException(nameof(searchEngine));
        _settingsService = settingsService;
        CurrentSelectionSignature = SelectionSignature.From(snapshot);

        if (!IsExecuting)
        {
            HasExecutionResult = false;
            ClearUndo();
            CurrentOutputPaths = Array.Empty<string>();
            ExecutionResultMessage = null;
            ExecutionStatusMessage = null;
        }

        ContextSummary = GenerateSummary(snapshot);
        ContextNotice = GenerateNotice(snapshot);
        OnPropertyChanged(nameof(IsContextAvailable));
        if (!IsExecuting)
        {
            SearchTask = UpdateFilteredActionsAsync();
        }
    }

    public void RefreshPackVisibility()
    {
        if (IsExecuting) return;
        SelectionRevision++;
        PublishResults(ActionGrouping.Create(VisibleSearchResults, !string.IsNullOrWhiteSpace(_searchText), IncludeUnavailableActions), preserveSelection: true);
    }
    public Task RefreshActionsAsync()
    {
        if (IsExecuting) return Task.CompletedTask;
        return SearchTask = UpdateFilteredActionsAsync();
    }
    public void ShowPackNotice(string message) => ContextNotice = message;

    public void RequestCancelConfirmation()
    {
        if (IsExecuting)
        {
            ShowCancelConfirmation = true;
        }
    }

    public void CancelExecution()
    {
        if (IsExecuting && _executionCts != null)
        {
            _executionCts.Cancel();
        }
    }

    public void ConfirmCancel()
    {
        if (IsExecuting && _executionCts != null)
        {
            _executionCts.Cancel();
        }
        ShowCancelConfirmation = false;
    }

    public void DismissCancelConfirmation()
    {
        ShowCancelConfirmation = false;
    }

    /// <summary>
    /// Asks the selected action for a preview with the given options. Never throws for
    /// action failures: a broken preview is reported as blocked without raw details.
    /// </summary>
    public async Task<ActionPreview?> PreviewSelectedActionAsync(ActionParameters parameters, CancellationToken cancellationToken = default,
        ContextSnapshot? expectedSnapshot = null, IAction? expectedAction = null)
    {
        if (_selectedAction == null || _currentSnapshot == null || _actionRegistry == null)
            return null;
        if (expectedSnapshot is not null && (!ReferenceEquals(expectedSnapshot, _currentSnapshot) || expectedSnapshot.IsStale))
            return new ActionPreview(false, Strings.GetString("PackInvalidSelection"), [], [], IsBlocked: true);

        var action = _actionRegistry.GetAction(_selectedAction.Action.Descriptor.Id);
        if (action == null) return null;
        if (expectedAction is not null && !ReferenceEquals(expectedAction, action))
            return new ActionPreview(false, Strings.GetString("PackInvalidSelection"), [], [], IsBlocked: true);

        try
        {
            return await action.PreviewAsync(_currentSnapshot, parameters ?? new EmptyParameters(), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new ActionPreview(false, Strings.GetString("Preview_NotAvailable"), Array.Empty<string>(), Array.Empty<string>(), IsBlocked: true);
        }
    }

    public async Task ExecuteSelectedActionAsync(ActionParameters? parameters = null)
    {
        if (IsExecuting || _isUndoing || _selectedAction == null || !_selectedAction.IsCompatible || _currentSnapshot == null || _actionRegistry == null)
            return;

        var action = _actionRegistry.GetAction(_selectedAction.Action.Descriptor.Id);
        if (action == null) return;

        IsExecuting = true;
        ShowCancelConfirmation = false;
        HasExecutionResult = false;
        ClearUndo();
        CurrentOutputPaths = Array.Empty<string>();
        ExecutionResultMessage = null;
        OutputCount = 0;
        FailureCount = 0;
        ExecutionProgress = 0;
        ExecutionStatusMessage = Strings.GetString("Execution_Starting");

        _executionCts?.Dispose();
        var runCts = new CancellationTokenSource();
        _executionCts = runCts;

        var progress = new Progress<ActionProgress>(p =>
        {
            if (_executionCts != runCts) return;
            if (double.IsFinite(p.Percentage))
            {
                ExecutionProgress = Math.Clamp(p.Percentage * 100.0, 0, 100);
            }
            if (p.StatusMessage != null)
                ExecutionStatusMessage = p.StatusMessage;
        });

        try
        {
            var receipt = await action.ExecuteAsync(_currentSnapshot, parameters ?? new EmptyParameters(), progress, runCts.Token);

            if (_executionCts != runCts) return;

            if (receipt.IsUndoable && receipt.UndoOperationId is { } undoId)
            { _undoOperationId = undoId; _undoUntil = receipt.UndoUntil; }
            ExecutionIsSuccess = receipt.IsSuccess;
            CurrentOutputPaths = receipt.OutputPaths;
            OutputCount = receipt.OutputPaths.Count;
            FailureCount = receipt.Failures.Count;
            OnPropertyChanged(nameof(CanOpenSingleOutput));

            if (receipt.Message != null)
            {
                ExecutionResultMessage = receipt.Message;
            }
            else
            {
                if (receipt.IsSuccess)
                {
                    if (OutputCount > 0)
                    {
                        ExecutionResultMessage = string.Format(Strings.GetString(OutputCount == 1 ? "Execution_Completed_Outputs_One" : "Execution_Completed_Outputs_Other"), OutputCount);
                    }
                    else
                    {
                        ExecutionResultMessage = Strings.GetString("Execution_Completed");
                    }
                }
                else
                {
                    if (FailureCount > 0)
                    {
                        if (OutputCount > 0)
                        {
                            ExecutionResultMessage = string.Format(Strings.GetString(OutputCount == 1 ? "Execution_CompletedPartially_Outputs_One" : "Execution_CompletedPartially_Outputs_Other"), OutputCount, FailureCount);
                        }
                        else
                        {
                            ExecutionResultMessage = string.Format(Strings.GetString(FailureCount == 1 ? "Execution_Failed_Items_One" : "Execution_Failed_Items_Other"), FailureCount);
                        }
                    }
                    else
                    {
                        ExecutionResultMessage = Strings.GetString("Execution_Failed");
                    }
                }
            }

            if (!receipt.IsSuccess && receipt.Failures.Any())
            {
                var messages = string.Join(" | ", receipt.Failures.Where(f => !string.IsNullOrEmpty(f.ErrorMessage)).Select(f => f.ErrorMessage));
                if (!string.IsNullOrEmpty(messages))
                {
                    ExecutionResultMessage = $"{ExecutionResultMessage} ({messages})";
                }
            }

            if (receipt.IsSuccess && receipt.ClipboardText != null)
            {
                if (System.Text.Encoding.UTF8.GetByteCount(receipt.ClipboardText) > 10 * 1024 * 1024)
                {
                    ExecutionResultMessage += " " + Strings.GetString("Execution_ClipboardLimitExceeded");
                }
                else if (ClipboardWriter != null)
                {
                    string note;
                    try
                    {
                        await ClipboardWriter.WriteTextAsync(receipt.ClipboardText);
                        note = Strings.GetString("Execution_CopiedToClipboard");
                    }
                    catch (Exception)
                    {
                        // Another application may hold the clipboard; the action itself succeeded.
                        note = Strings.GetString("Execution_ClipboardCopyFailed");
                    }
                    ExecutionResultMessage += string.IsNullOrEmpty(ExecutionResultMessage) ? note : " " + note;
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (_executionCts != runCts) return;
            ExecutionIsSuccess = false;
            ExecutionResultMessage = Strings.GetString("Execution_Canceled");
        }
        catch (Exception)
        {
            if (_executionCts != runCts) return;
            ExecutionIsSuccess = false;
            ExecutionResultMessage = Strings.GetString("Execution_Error_Generic");
        }
        finally
        {
            if (_executionCts == runCts)
            {
                _executionCts = null;
                IsExecuting = false;
                RefreshUndoAvailability();
                ShowCancelConfirmation = false;

                if (ExecutionIsSuccess || CurrentOutputPaths.Count > 0)
                {
                    if (CurrentSelectionSignature != null)
                    {
                        _settingsService?.RecordSelectionUsage(CurrentSelectionSignature.Key, action.Descriptor.Id, DateTimeOffset.UtcNow);
                        _searchEngine?.RefreshSignals();
                    }
                }

                var afterAction = (SettingsService ?? _settingsService)?.Settings.AfterFileAction ?? ContextBar.Application.Configuration.AfterFileAction.KeepResultVisible;

                HasExecutionResult = true;

                if (ExecutionIsSuccess && !CanUndoResult && action.Descriptor.ChangesFiles && afterAction == ContextBar.Application.Configuration.AfterFileAction.ClosePalette)
                {
                    Done();
                }
            }
        }
    }

    private async Task UpdateFilteredActionsAsync()
    {
        if (_searchEngine == null || _currentSnapshot == null)
            return;

        // An earlier search can complete after a newer search even if cancellation
        // was requested, so cancellation alone is not sufficient.
        var revision = Interlocked.Increment(ref _searchRevision);
        var previous = _searchCts;
        var current = new CancellationTokenSource();
        _searchCts = current;
        previous?.Cancel();
        var query = _searchText;
        var snapshot = _currentSnapshot;
        var searchEngine = _searchEngine;
        SearchError = null;
        OnPropertyChanged(nameof(SearchError));
        _isSearching = true;
        OnPropertyChanged(nameof(HasNoResults));

        try
        {
            // Intentionally preserve the caller's synchronization context so
            // WinUI-bound properties are updated on the UI thread.
            var results = await searchEngine.SearchAsync(query, snapshot, current.Token);
            if (revision != Interlocked.Read(ref _searchRevision) || current.IsCancellationRequested)
                return;

            _searchResults = results;
            PublishResults(ActionGrouping.Create(VisibleSearchResults, !string.IsNullOrWhiteSpace(query), IncludeUnavailableActions));
        }
        catch (OperationCanceledException) when (current.IsCancellationRequested)
        {
            // Superseded input; the latest invocation owns the displayed results.
        }
        catch (Exception)
        {
            if (revision != Interlocked.Read(ref _searchRevision))
                return;

            // Do not leak selected data or raw exception messages to the UI.
            SearchError = ContextBar.Application.Resources.Strings.Search_ErrorDefault;
            _searchResults = Array.Empty<ActionSearchResult>();
            PublishResults(Array.Empty<ActionGroup>());
            OnPropertyChanged(nameof(SearchError));
        }
        finally
        {
            if (ReferenceEquals(_searchCts, current))
            {
                _searchCts = null;
                _isSearching = false;
                OnPropertyChanged(nameof(HasNoResults));
            }
            current.Dispose();
        }
    }

    private void PublishResults(IReadOnlyList<ActionGroup> groups, bool preserveSelection = false)
    {
        var actions = groups.SelectMany(g => g.Items).ToArray();
        var selected = preserveSelection
            ? actions.FirstOrDefault(r => r.Action.Descriptor.Id == _selectedAction?.Action.Descriptor.Id)
            : null;
        _groups = groups;
        _filteredActions = actions;
        _selectedAction = selected ?? actions.FirstOrDefault();
        ResultsRevision++;
        OnPropertyChanged(nameof(ResultsRevision));
        OnPropertyChanged(nameof(Groups));
        OnPropertyChanged(nameof(FilteredActions));
        OnPropertyChanged(nameof(SelectedAction));
        OnPropertyChanged(nameof(UnavailableActionCount));
        OnPropertyChanged(nameof(HasUnavailableActions));
        OnPropertyChanged(nameof(ShowUnavailableLabel));
        OnPropertyChanged(nameof(HasNoResults));
    }

    private void ClearExecutionResultForBrowsing()
    {
        if (IsExecuting || !HasExecutionResult) return;
        HasExecutionResult = false;
        ClearUndo();
        CurrentOutputPaths = Array.Empty<string>();
        OutputCount = 0;
        FailureCount = 0;
        ExecutionResultMessage = null;
        OnPropertyChanged(nameof(CanOpenSingleOutput));
    }

    private string GenerateSummary(ContextSnapshot snapshot)
    {
        // Empty snapshots carry the app's own states ("Ready", "Loading context..."). Failure
        // messages come from providers, are technical and not localized: the palette shows a
        // short status here and a localized explanation in ContextNotice instead.
        if (snapshot.Status == ContextStatus.Empty)
            return string.IsNullOrEmpty(snapshot.UserMessage) ? Strings.Context_None : snapshot.UserMessage;
        if (snapshot.Status != ContextStatus.Available)
            return Strings.Context_Unavailable;

        return snapshot.Content switch
        {
            FileSelection fs => GenerateFileSelectionSummary(fs),
            TextSelection => Strings.GetString("Context_TextSelected"),
            TableData table => GenerateTableSummary(table),
            _ => Strings.GetString("Context_UnknownSelection")
        };
    }

    private static string? GenerateNotice(ContextSnapshot snapshot) => snapshot.Status switch
    {
        ContextStatus.Available => null,
        ContextStatus.Empty => null,
        ContextStatus.Unsupported => Strings.Notice_Unsupported,
        ContextStatus.Denied => Strings.Notice_Denied,
        ContextStatus.TimedOut => Strings.Notice_TimedOut,
        _ => Strings.Notice_Failed,
    };

    private string GenerateFileSelectionSummary(FileSelection fs)
    {
        int images = 0;
        int folders = 0;
        int other = 0;
        int offlineOrInaccessible = 0;

        foreach (var file in fs.Files)
        {
            if (file.IsDirectory)
            {
                folders++;
            }
            else if (file.MediaType != null && file.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                images++;
            }
            else
            {
                other++;
            }

            if (!file.IsAvailableLocally || !file.IsAccessible)
            {
                offlineOrInaccessible++;
            }
        }

        var parts = new List<string>();

        if (images > 0)
        {
            parts.Add(images == 1 ? ContextBar.Application.Resources.Strings.Summary_ImagesSingular : string.Format(ContextBar.Application.Resources.Strings.Summary_ImagesPlural, images));
        }

        if (folders > 0)
        {
            parts.Add(folders == 1 ? ContextBar.Application.Resources.Strings.Summary_FoldersSingular : string.Format(ContextBar.Application.Resources.Strings.Summary_FoldersPlural, folders));
        }

        if (other > 0)
        {
            parts.Add(other == 1 ? ContextBar.Application.Resources.Strings.Summary_FilesSingular : string.Format(ContextBar.Application.Resources.Strings.Summary_FilesPlural, other));
        }

        if (parts.Count == 0)
        {
            parts.Add(string.Format(ContextBar.Application.Resources.Strings.Summary_FilesPlural, 0));
        }

        var summary = string.Join(", ", parts);

        if (offlineOrInaccessible > 0)
        {
            summary += string.Format(ContextBar.Application.Resources.Strings.Summary_OfflineStatus, offlineOrInaccessible);
        }

        return summary;
    }

    private string GenerateTableSummary(TableData table)
    {
        int rows = table.Rows.Count;
        int cols = 0;
        foreach (var row in table.Rows)
        {
            if (row.Count > cols)
            {
                cols = row.Count;
            }
        }

        return string.Format(ContextBar.Application.Resources.Strings.Summary_TableDimensions, rows, cols);
    }

    public string? GetBadgeText(ActionSearchResult result)
    {
        if (result.Highlight == ActionHighlight.None)
            return null;

        if (result.Highlight == ActionHighlight.Favorite)
            return Strings.Badge_Favorite;

        if (result.Highlight == ActionHighlight.BestFit)
            return Strings.Badge_Recommended;

        if (result.Highlight == ActionHighlight.LastUsedForSelection && CurrentSelectionSignature != null)
        {
            var sig = CurrentSelectionSignature;
            bool plural = sig.Count != CountBucket.One;
            string family = "Item";
            if (sig.Kind == ContentKind.Files && sig.Families.Count == 1)
            {
                family = sig.Families[0] switch
                {
                    SelectionFamily.Text or SelectionFamily.Other => "File",
                    var known => known.ToString(),
                };
            }
            else if (sig.Kind == ContentKind.Files)
            {
                family = "File";
            }
            else if (sig.Kind == ContentKind.Table)
            {
                family = "Table";
            }
            else if (sig.Kind == ContentKind.Text)
            {
                family = "Text";
            }

            string countStr = sig.Count switch
            {
                CountBucket.One => "1",
                CountBucket.Few => "2–5",
                CountBucket.Several => "6–20",
                CountBucket.Many => "21+",
                _ => "1"
            };

            string key = $"Badge_LastUsedFor_{family}_{(plural ? "Other" : "One")}";
            return string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.GetString(key), countStr);
        }

        return null;
    }

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private static ActionDescriptor Descriptor(string id, string title, string description, params string[] terms) =>
        new(id, title, description, new HashSet<ContentKind> { ContentKind.Files }, terms, true, true);

    private class FakeAction : IAction
    {
        private readonly ActionAvailability _availability;
        public ActionDescriptor Descriptor { get; }
        public FakeAction(ActionDescriptor descriptor, ActionAvailability? availability = null)
        {
            Descriptor = descriptor;
            _availability = availability ?? new ActionAvailability(true);
        }

        public ValueTask<ActionAvailability> MatchAsync(ContextSnapshot context, CancellationToken cancellationToken) =>
            new ValueTask<ActionAvailability>(_availability);

        public ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters parameters, CancellationToken cancellationToken) =>
            new ValueTask<ActionPreview>(new ActionPreview(false, null, Array.Empty<string>(), Array.Empty<string>()));

        public Task<ActionReceipt> ExecuteAsync(ContextSnapshot context, ActionParameters parameters, IProgress<ActionProgress>? progress, CancellationToken cancellationToken) =>
            Task.FromResult(new ActionReceipt(true, false, Array.Empty<string>(), Array.Empty<ActionFailure>(), false));
    }

    private class FakeSlowAction : IAction
    {
        public ActionDescriptor Descriptor { get; }
        public FakeSlowAction(ActionDescriptor descriptor) => Descriptor = descriptor;

        public ValueTask<ActionAvailability> MatchAsync(ContextSnapshot context, CancellationToken cancellationToken) =>
            new ValueTask<ActionAvailability>(new ActionAvailability(true));

        public ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters parameters, CancellationToken cancellationToken) =>
            new ValueTask<ActionPreview>(new ActionPreview(false, null, Array.Empty<string>(), Array.Empty<string>()));

        public async Task<ActionReceipt> ExecuteAsync(ContextSnapshot context, ActionParameters parameters, IProgress<ActionProgress>? progress, CancellationToken cancellationToken)
        {
            for (int i = 0; i <= 10; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new ActionProgress((double)i / 10, null, $"Processing item {i} of 10..."));
                await Task.Delay(200, cancellationToken); // Simulate slow work
            }

            return new ActionReceipt(true, false, Array.Empty<string>(), Array.Empty<ActionFailure>(), false, "Slow operation finished.");
        }
    }

    private class FakeFailAction : IAction
    {
        public ActionDescriptor Descriptor { get; }
        public FakeFailAction(ActionDescriptor descriptor) => Descriptor = descriptor;

        public ValueTask<ActionAvailability> MatchAsync(ContextSnapshot context, CancellationToken cancellationToken) =>
            new ValueTask<ActionAvailability>(new ActionAvailability(true));

        public ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters parameters, CancellationToken cancellationToken) =>
            new ValueTask<ActionPreview>(new ActionPreview(false, null, Array.Empty<string>(), Array.Empty<string>()));

        public Task<ActionReceipt> ExecuteAsync(ContextSnapshot context, ActionParameters parameters, IProgress<ActionProgress>? progress, CancellationToken cancellationToken)
        {
            return Task.FromResult(new ActionReceipt(false, false, Array.Empty<string>(), new[] {
                new ActionFailure(null, "Simulated failure occurred.")
            }, false, "Operation failed."));
        }
    }

    public static PaletteViewModel CreateFake(IReadOnlyDictionary<string, IReadOnlyList<string>>? aliases = null)
    {
        if (aliases == null)
        {
            var basePath = AppContext.BaseDirectory;
            var defaultPath = System.IO.Path.Combine(basePath, "aliases.json");
            aliases = ActionAliasLoader.LoadAliases(defaultPath);
        }

        var vm = new PaletteViewModel();

        var fakeSnapshot = new ContextSnapshot(
            Guid.NewGuid(),
            new SourceWindowIdentity(IntPtr.Zero, 1234, "explorer.exe", DateTimeOffset.Now, "Fake Window Title"),
            ContextStatus.Available,
            new FileSelection(new[]
            {
                new SelectedFile("C:\\Fake\\image1.png", "image1.png", ".png", "image/png", 1024, true, true, null, false),
                new SelectedFile("C:\\Fake\\image2.png", "image2.png", ".png", "image/png", 2048, true, true, null, false)
            }),
            DateTimeOffset.Now,
            DateTimeOffset.Now.AddMinutes(5),
            "FakeProvider",
            null
        );

        var fakeActions = new List<ActionDescriptor>
        {
            new ActionDescriptor(
                "contextbar.image.convert-webp",
                "Convert to WebP",
                "Converts the selected images to WebP format.",
                new HashSet<ContentKind> { ContentKind.Files },
                new[] { "image", "convert", "webp" },
                false,
                true),
            new ActionDescriptor(
                "contextbar.image.convert-avif",
                "Convert to AVIF",
                "Converts the selected images to AVIF format.",
                new HashSet<ContentKind> { ContentKind.Files },
                new[] { "image", "convert", "avif" },
                false,
                true),
            new ActionDescriptor(
                "contextbar.image.resize",
                "Resize Image",
                "Resizes the selected images.",
                new HashSet<ContentKind> { ContentKind.Files },
                new[] { "image", "resize", "scale" },
                false,
                true),
            new ActionDescriptor(
                "contextbar.general.settings",
                "Settings",
                Strings.Action_OpenSettings_Description,
                new HashSet<ContentKind> { ContentKind.None },
                new[] { "config", "preferences" },
                false,
                false)
        };

        var registry = new ActionRegistry();
        foreach (var desc in fakeActions)
        {
            registry.Register(new FakeAction(desc));
        }

        registry.Register(new FakeSlowAction(new ActionDescriptor(
            "contextbar.fake.slow",
            "Slow Action",
            "A slow mock action to test progress updates and cancellation.",
            new HashSet<ContentKind> { ContentKind.None },
            new[] { "fake", "slow", "test" },
            false, false)));

        registry.Register(new FakeFailAction(new ActionDescriptor(
            "contextbar.fake.fail",
            "Failing Action",
            "A mock action that always fails.",
            new HashSet<ContentKind> { ContentKind.None },
            new[] { "fake", "fail", "test" },
            false, false)));
        // Stand-ins for pack actions with the same ids and titles as the real ones.
        // Real packs are composed by the desktop host; the view model stays engine-free.
        fakeActions.RemoveAll(a => a.Id == "contextbar.image.convert-webp" || a.Id == "contextbar.image.resize");
        var imageStandIns = new[]
        {
            Descriptor("contextbar.image.convert-webp", "Convert to WebP", "Converts the selected images to WebP format.", "image", "convert", "webp"),
            Descriptor("contextbar.image.remove-metadata", "Remove Image Metadata", "Creates copies without privacy-sensitive metadata.", "image", "metadata", "exif", "strip", "privacy"),
            Descriptor("contextbar.image.resize", "Resize Images", "Creates resized copies of the selected images.", "image", "resize", "scale")
        };
        foreach (var descriptor in imageStandIns)
        {
            registry.Register(new FakeAction(descriptor));
            fakeActions.Add(descriptor);
        }

        var videoStandIns = new[]
        {
            Descriptor("contextbar.video.extract-audio", "Extract Audio from Video", "Saves the audio track of the selected videos as MP3 files.", "extract", "audio", "mp3", "video"),
            Descriptor("contextbar.video.convert-mp4", "Convert to MP4", "Converts the selected videos to MP4.", "video", "convert", "mp4"),
            Descriptor("contextbar.video.convert-webm", "Convert to WEBM", "Converts the selected videos to WEBM.", "video", "convert", "webm")
        };
        foreach (var descriptor in videoStandIns)
        {
            registry.Register(new FakeAction(descriptor, new ActionAvailability(false, "No supported video files in the selection.")));
            fakeActions.Add(descriptor);
        }

        var searchEngine = new ActionSearchEngine(registry, aliases);

        vm.Initialize(fakeSnapshot, registry, searchEngine);
        return vm;
    }
}
