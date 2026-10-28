using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using PowerTrove.Application.ViewModels;
using PowerTrove.Application.Search;
using PowerTrove.Core;
using PowerTrove.Core.Search;
using PowerTrove.Core.Catalog;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Globalization;
using PowerTrove.Application.Packs;
using PowerTrove.Application.Media;

namespace PowerTrove.Desktop;

// WinUI 3 x:Bind requires properties to be public on the bound type, and records with x:Bind sometimes have issues.
// So we wrap the ActionDescriptor for the view.
public class ActionDescriptorProxy
{
    private readonly ActionDescriptor _descriptor;
    public string Title => _descriptor.Title;
    public string Description => _descriptor.Description;
    public ActionDescriptor Original => _descriptor;

    public ActionDescriptorProxy(ActionDescriptor descriptor)
    {
        _descriptor = descriptor;
    }
}

public class ActionSearchResultProxy
{
    private readonly ActionSearchResult _result;
    public string Title => _result.Action.Descriptor.Title;
    public string Description => _result.Action.Descriptor.Description;
    public bool IsCompatible => _result.IsCompatible;
    public string? IncompatibilityReason => _result.IncompatibilityReason;
    public string? AvailabilityReason => _result.AvailabilityReason;
    public bool HasAvailabilityReason => !string.IsNullOrEmpty(AvailabilityReason);
    public ActionSearchResult Original => _result;

    /// <summary>Segoe Fluent Icons glyph for the action's family.</summary>
    public string IconGlyph => _result.Action is CatalogAction ? "\uE896" : IconFor(_result.Action.Descriptor.Id);

    /// <summary>
    /// Why the action cannot run, shown under it. Hidden when the whole selection is unavailable:
    /// the palette then explains it once instead of repeating it on every action.
    /// </summary>
    public string? Reason { get; }

    public string? BadgeText { get; }
    public bool HasBadge => !string.IsNullOrEmpty(BadgeText);

    public string AccessibleName
    {
        get
        {
            if (_result.InstallState == PackInstallState.NotInstalled)
                return PowerTrove.Core.Resources.Strings.CatalogAccessibleName(Title,
                    Reason ?? (_result.Action as CatalogAction)?.DownloadReason ?? string.Empty);
            var name = _result.IsCompatible
                ? _result.Action.Descriptor.Title
                : $"{_result.Action.Descriptor.Title} (Unavailable: {_result.IncompatibilityReason})";
            if (HasBadge)
            {
                name += $" - {BadgeText}";
            }
            return name;
        }
    }

    public ActionSearchResultProxy(ActionSearchResult result, string? badgeText = null, bool contextAvailable = true)
    {
        _result = result;
        BadgeText = badgeText;
        if (contextAvailable)
            Reason = !result.IsCompatible ? result.IncompatibilityReason : result.AvailabilityReason;
    }

    internal static string IconFor(string actionId)
    {
        string family = actionId.StartsWith("powertrove.", StringComparison.Ordinal)
            ? actionId.Split('.') is { Length: > 2 } parts ? parts[1] : string.Empty
            : string.Empty;
        return family switch
        {
            "pdf" => "\uE8A5",      // Document
            "image" => "\uE91B",    // Photo
            "video" => "\uE714",    // Video
            "audio" => "\uE8D6",    // Audio
            "files" => "\uE8B7",    // Folder
            "table" => "\uE80A",    // Grid
            "recipe" => "\uE945",   // Lightning
            "general" => "\uE713",  // Settings
            _ => "\uE7C3",          // Page
        };
    }
}

public class ActionGroupProxy
{
    public string Title { get; }
    public string DisplayTitle { get; }
    public ObservableCollection<ActionSearchResultProxy> Items { get; }
    public bool HasTitle => !string.IsNullOrEmpty(Title);
    public string CountText => Items.Count.ToString(System.Globalization.CultureInfo.CurrentCulture);
    public string AccessibleName => UiText.Format(Items.Count == 1 ? UiText.GroupActionsOne : UiText.GroupActionsOther, Title, Items.Count);

    public ActionGroupProxy(string title, IEnumerable<ActionSearchResultProxy> items, bool compactTitle = false)
    {
        Title = title;
        int separator = title.IndexOf(" · ", StringComparison.Ordinal);
        DisplayTitle = compactTitle && separator >= 0 ? title[(separator + 3)..] : title;
        Items = new ObservableCollection<ActionSearchResultProxy>(items);
    }

    public override string ToString() => Title;
}

public partial class MainWindow : Window
{
    public PaletteViewModel ViewModel { get; }

    // Wrap the filtered actions in our proxy
    public ObservableCollection<ActionSearchResultProxy> FilteredActionsProxy { get; } = new();
    public ObservableCollection<ActionGroupProxy> GroupedActionsProxy { get; } = new();

    private readonly DispatcherTimer _undoTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    public ReleaseUpdatesViewModel Updates => ((App)App.Current).Updates;
    private async void DownloadContextPack_Click(object sender, RoutedEventArgs e)
    {
        if (((App)App.Current).UpdateInstalling || ViewModel.IsExecuting || sender is not FrameworkElement { Tag: PackSuggestion suggestion }) return;
        await ViewModel.ContextPacks.InstallAsync(suggestion);
    }
    private void DismissContextPack_Click(object sender, RoutedEventArgs e)
    { if (sender is FrameworkElement { Tag: string id }) ViewModel.ContextPacks.Dismiss(id); }
    private void CancelContextPack_Click(object sender, RoutedEventArgs e) => ViewModel.ContextPacks.Cancel();
    private bool _undoInProgress;
    public bool CanCloseForUpdate => !ViewModel.ContextPacks.IsBusy && !_undoInProgress && !ViewModel.IsExecuting && !_dialogOpen && !_enterController.IsPending && _activeVisualEditor is null;
    private async void InstallUpdate_Click(object sender, RoutedEventArgs e) => await Updates.InstallAsync();
    private void DismissUpdate_Click(object sender, RoutedEventArgs e) => Updates.DismissNotice();
    private void CancelUpdate_Click(object sender, RoutedEventArgs e) => Updates.CancelInstallation();

    public MainWindow()
    {
        ViewModel = new PaletteViewModel();
        this.InitializeComponent();
        Title = UiText.MainWindowTitle;
        ConfigureLauncherWindow();

        UpdateProxyList();

        // WinUI can raise Window.Activated before its XAML content is loaded.
        // Both Loaded and the deferred activation callback are used for first focus.
        this.Activated += MainWindow_Activated;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        ViewModel.DismissRequested += ViewModel_DismissRequested;

        _undoTimer.Tick += (_, _) => ViewModel.RefreshUndoAvailability();
        _undoTimer.Start();
        this.Closed += (_, _) => _undoTimer.Stop();
        this.Closed += MainWindow_Closed;
    }

    /// <summary>
    /// Borderless, non-resizable launcher with rounded corners and an acrylic backdrop that
    /// stays out of Alt+Tab; it hides when it loses focus (see MainWindow_Activated).
    /// </summary>
    private void ConfigureLauncherWindow()
    {
        ExtendsContentIntoTitleBar = true;
        SystemBackdrop = new Microsoft.UI.Xaml.Media.DesktopAcrylicBackdrop();
        AppWindow.IsShownInSwitchers = false;
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
            presenter.SetBorderAndTitleBar(true, false);
        }
        // Alt+F4 hides the launcher like Esc; only ExitApplication really closes it.
        AppWindow.Closing += (_, args) =>
        {
            if (AllowClose)
                return;
            args.Cancel = true;
            (App.Current as App)?.DismissPalette();
        };
        SetTitleBar(DragRegion);
        BrandIcon.Source = AppIconImage.Create(32);
        FirstRunIcon.Source = AppIconImage.Create(96);
    }

    /// <summary>PDF page actions that also offer the visual page organizer in their options dialog.</summary>
    private static readonly HashSet<string> VisualPageActions = new(StringComparer.Ordinal)
    {
        "powertrove.pdf.reorder", "powertrove.pdf.rotate", "powertrove.pdf.remove-pages", "powertrove.pdf.extract-pages"
    };

    /// <summary>Opens the visual page organizer for the PDFs in the current selection.</summary>
    private void OpenPageOrganizer()
    {
        var app = App.Current as App;
        var action = app?.Actions?.GetAction(PowerTrove.Core.Activation.ActivationProtocol.OrganizePagesActionId);
        var snapshot = ViewModel.CurrentSnapshot;
        if (app == null || app.UpdateInstalling || action == null || snapshot == null)
            return;
        app.DismissPalette();
        var organizer = new PageOrganizerWindow(action, snapshot);
        app.TrackEditor(organizer);
        app.ApplyWindowIcon(organizer);
        organizer.Activate();
    }

    /// <summary>Set by the app when exiting; otherwise closing the window only hides it.</summary>
    public bool AllowClose { get; set; }

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => OpenSettingsFromPalette();

    private void OpenSettingsFromPalette()
    {
        if (App.Current is App app)
        {
            app.DismissPalette();
            app.OpenSettings();
        }
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _packInstallCancellation?.Cancel();
        ViewModel.ContextPacks.Dispose();
        _activeVisualEditor?.Close();
        if (App.Current is App app)
        {
            app.DisposeHotkeyService();
        }
    }


    private void ViewModel_DismissRequested(object? sender, EventArgs e)
    {
        if (App.Current is App app)
        {
            app.DismissPalette();
        }
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (!ViewModel.IsExecuting && !_dialogOpen && (e.PropertyName == nameof(PaletteViewModel.SearchText)
            || _observedSelectionRevision != ViewModel.SelectionRevision))
            InvalidatePendingEnter();
        _observedSelectionRevision = ViewModel.SelectionRevision;
        if (e.PropertyName == nameof(PaletteViewModel.ResultsRevision))
        {
            UpdateProxyList();
        }
        else if (e.PropertyName == nameof(PaletteViewModel.SelectedAction))
        {
            UpdateSelection();
        }

        var notice = e.PropertyName switch
        {
            nameof(PaletteViewModel.HotkeyWarningMessage) => HotkeyNoticeText,
            nameof(PaletteViewModel.CatalogWarningMessage) => CatalogNoticeText,
            nameof(PaletteViewModel.ContextNotice) => ContextNoticeText,
            nameof(PaletteViewModel.HasExecutionResult) or nameof(PaletteViewModel.ExecutionResultMessage)
                when ViewModel.HasExecutionResult => ExecutionResultText,
            _ => null
        };
        if (notice is not null)
            AnnounceNotice(notice);
    }

    private void AnnounceNotice(TextBlock notice)
    {
        var message = notice.Text;
        if (string.IsNullOrWhiteSpace(message)) return;
        // Let bindings/layout settle before announcing; ignore cleared or superseded messages.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (AllowClose || !AppWindow.IsVisible || !notice.IsLoaded || notice.Text != message
                || (notice == ExecutionResultText && !ViewModel.HasExecutionResult)) return;
            var peer = FrameworkElementAutomationPeer.FromElement(notice)
                ?? FrameworkElementAutomationPeer.CreatePeerForElement(notice);
            peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        });
    }

    private bool _updatingProxyList;
    private bool _scrollToStartPending = true;
    private long _listRevision;
    private bool _listPositionQueued;

    private void UpdateProxyList()
    {
        _listRevision++;
        _updatingProxyList = true;
        try
        {
            ActionList.SelectedItem = null;
            FilteredActionsProxy.Clear();
            GroupedActionsProxy.Clear();
            // In a single-domain view, repeating "Images ·" on every section adds no information.
            // Mixed-domain groups retain their prefixes; full names remain available to Narrator.
            var domains = ViewModel.Groups.Select(g => g.Title.Split(" · ", StringSplitOptions.None))
                .Where(parts => parts.Length > 1).Select(parts => parts[0]).Distinct(StringComparer.Ordinal).ToArray();
            foreach (var group in ViewModel.Groups)
            {
                var proxies = group.Items.Select(item => new ActionSearchResultProxy(item, ViewModel.GetBadgeText(item), ViewModel.IsContextAvailable)).ToArray();
                foreach (var proxy in proxies) FilteredActionsProxy.Add(proxy);
                GroupedActionsProxy.Add(new ActionGroupProxy(group.Title, proxies, compactTitle: domains.Length == 1));
            }
            UpdateSelection(resetScroll: true);
        }
        finally { _updatingProxyList = false; }
    }

    private void UpdateSelection(bool resetScroll = false)
    {
        var proxy = FilteredActionsProxy.FirstOrDefault(p => p.Original.Action.Descriptor.Id == ViewModel.SelectedAction?.Action.Descriptor.Id);
        bool changed = !ReferenceEquals(ActionList.SelectedItem, proxy);
        bool wasUpdating = _updatingProxyList;
        _updatingProxyList = true;
        try { ActionList.SelectedItem = proxy; }
        finally { _updatingProxyList = wasUpdating; }
        if (resetScroll) _scrollToStartPending = ReferenceEquals(proxy, FilteredActionsProxy.FirstOrDefault());
        else if (changed) _scrollToStartPending = false;
        QueueListPosition();
    }

    private void QueueListPosition()
    {
        if (_listPositionQueued) return;
        _listPositionQueued = true;
        long revision = _listRevision;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _listPositionQueued = false;
            if (revision != _listRevision) { QueueListPosition(); return; }
            if (!AppWindow.IsVisible || !ActionList.IsLoaded) return;
            ActionList.UpdateLayout();
            if (_scrollToStartPending)
            {
                // ScrollIntoView alone may hide the first group header or retain an old offset.
                if (FindDescendant<ScrollViewer>(ActionList) is { } scroller)
                {
                    scroller.ChangeView(null, 0, null, disableAnimation: true);
                    _scrollToStartPending = false;
                }
            }
            else if (ActionList.SelectedItem is ActionSearchResultProxy selected)
                ActionList.ScrollIntoView(selected);
        });
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } descendant) return descendant;
        }
        return null;
    }

    private void ActionList_Loaded(object sender, RoutedEventArgs e) => QueueListPosition();

    private void ActionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingProxyList) return;
        if (ActionList.SelectedItem is ActionSearchResultProxy proxy)
        {
            _scrollToStartPending = false;
            if (ViewModel.SelectedAction?.Action.Descriptor.Id != proxy.Original.Action.Descriptor.Id)
                ViewModel.SelectedAction = proxy.Original;
        }
    }

    public void PositionPalette(nint sourceHwnd, PowerTrove.Platform.Windows.Displays.IWindowMonitorService monitorService)
    {
        var workArea = monitorService.GetMonitorWorkAreaForWindow(sourceHwnd);
        var rect = PowerTrove.Platform.Windows.Displays.PlacementCalculator.CalculateLauncherPlacement(700, 500, workArea);
        // Every fresh invocation has a predictable position on its captured source monitor.
        // Do not treat programmatic moves/DPI changes as a user-dragged position.
        AppWindow.MoveAndResize(rect);
    }

    public void ShowPalette()
    {
        if (TryActivateVisualEditor()) return;
        _focusSearchPending = !ViewModel.IsExecuting && !ViewModel.HasExecutionResult;
        if (_focusSearchPending && !_dialogOpen) InvalidatePendingEnter();
        _scrollToStartPending = _focusSearchPending;
        AppWindow.Show(activateWindow: true);
        Activate();
        // Activate() alone is often refused by the foreground lock; take the keyboard explicitly.
        ForegroundActivator.BringToFront(this);
        QueueSearchFocus();
        // Releasing Alt (Alt+Space) can move focus once more; focus the search box again shortly after.
        var refocus = DispatcherQueue.CreateTimer();
        refocus.Interval = TimeSpan.FromMilliseconds(150);
        refocus.IsRepeating = false;
        refocus.Tick += (_, _) => { if (AppWindow.IsVisible && !_dialogOpen && FocusManager.GetFocusedElement(Content.XamlRoot) is not TextBox) { _focusSearchPending = !ViewModel.IsExecuting && !ViewModel.HasExecutionResult; QueueSearchFocus(); } };
        refocus.Start();
        QueueListPosition();
    }

    public void HidePalette()
    {
        _packInstallCancellation?.Cancel();
        InvalidatePendingEnter();
        _focusSearchPending = false;
        _isWindowActive = false;
        _activatedSinceShown = false;
        _listRevision++;
        AppWindow.Hide();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.HasOutputs && ViewModel.CurrentOutputPaths.Count > 0)
        {
            var firstPath = ViewModel.CurrentOutputPaths[0];
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{firstPath}\"",
                    UseShellExecute = true
                });
            }
            catch
            {
                // Ignore if explorer fails to open
            }
        }
    }

    private async void ShowInExplorer_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.RevealOutputsAsync();
    }

    private async void OpenSingle_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.OpenSingleOutputAsync();
    }

    private async void UndoResult_Click(object sender, RoutedEventArgs e)
    {
        if (((App)App.Current).UpdateInstalling || _undoInProgress) return;
        _undoInProgress = true;
        try { await ViewModel.UndoResultAsync(); }
        finally { _undoInProgress = false; }
    }

    private void Done_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.Done();
    }

    private bool _focusSearchPending = true;
    private bool _isWindowActive;
    private bool _dialogOpen;
    private MediaTrimWindow? _activeVisualEditor;

    /// <summary>Resurface the current editor without reopening the palette or recapturing a target.</summary>
    public bool TryActivateVisualEditor()
    {
        if (_activeVisualEditor is not { } editor) return false;
        editor.Activate();
        ForegroundActivator.BringToFront(editor);
        return true;
    }
    private CancellationTokenSource? _packInstallCancellation;
    private bool _activatedSinceShown;
    private readonly PaletteEnterController _enterController = new();
    private long _observedSelectionRevision;

    private void InvalidatePendingEnter() => _enterController.Invalidate();

    private void SearchBox_Loaded(object sender, RoutedEventArgs e) => QueueSearchFocus();

    private void QueueSearchFocus()
    {
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (!_focusSearchPending || !_isWindowActive || !AppWindow.IsVisible || _dialogOpen) return;
            if (ViewModel.ShowFirstRun)
            {
                if (FirstRunGetStartedButton.IsLoaded && FirstRunGetStartedButton.Focus(FocusState.Keyboard))
                    _focusSearchPending = false;
                return;
            }
            if (ViewModel.IsExecuting || ViewModel.HasExecutionResult || !SearchBox.IsLoaded || !SearchBox.IsEnabled) return;
            if (SearchBox.Focus(FocusState.Keyboard))
            {
                SearchBox.SelectAll();
                _focusSearchPending = false;
            }
        });
    }

    private void DismissFirstRun_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.DismissFirstRun();
        (App.Current as App)?.SettingsService?.MarkFirstRunSeen();
        _focusSearchPending = true;
        QueueSearchFocus();
    }

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        _isWindowActive = args.WindowActivationState != WindowActivationState.Deactivated;
        if (!_isWindowActive)
        {
            if (_activatedSinceShown && AppWindow.IsVisible && !_dialogOpen && !ViewModel.IsExecuting
                && !ViewModel.ShowCancelConfirmation && App.Current is App app)
                app.DismissPalette();
            return;
        }
        _activatedSinceShown = true;
        // A focus call during Loaded can succeed before top-level activation. Retry after activation.
        QueueSearchFocus();
    }

    private void ConfirmCancel_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ConfirmCancel();
    }

    private void DismissCancel_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.DismissCancelConfirmation();
        // Restore focus appropriately
        SearchBox.Focus(FocusState.Programmatic);
    }

    private async void RetrySearch_Click(object sender, RoutedEventArgs e)
    {
        if (await _enterController.RetryAsync(ViewModel, () => AppWindow.IsVisible && !_dialogOpen))
            SearchBox.Focus(FocusState.Keyboard);
    }

    private async void PaletteRoot_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_dialogOpen) return;

        // Handle Escape before TextBox/ListView can consume their KeyDown.
        // Works regardless of which descendant currently holds keyboard focus.
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            if (ViewModel.IsExecuting)
            {
                if (ViewModel.ShowCancelConfirmation)
                {
                    ViewModel.DismissCancelConfirmation();
                    SearchBox.Focus(FocusState.Programmatic);
                }
                else
                {
                    ViewModel.RequestCancelConfirmation();
                }
            }
            else if (ViewModel.HasExecutionResult)
            {
                ViewModel.Done();
            }
            else
            {
                if (App.Current is App app)
                    app.DismissPalette();
            }
            return;
        }

        if (ViewModel.IsExecuting)
        {
            // Ignore other key inputs while executing
            return;
        }

        bool actionInput = IsWithin(e.OriginalSource as DependencyObject, SearchBox) || IsWithin(e.OriginalSource as DependencyObject, ActionList);

        if (Updates.IsInstalling && actionInput) { e.Handled = true; return; }

        if (ViewModel.HasExecutionResult)
        {
            if (e.Key == Windows.System.VirtualKey.Enter && actionInput)
            {
                e.Handled = true;
                _ = ViewModel.RevealOutputsAsync();
                return;
            }
        }

        if (e.Key == Windows.System.VirtualKey.O &&
            Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
        {
            e.Handled = true;
            if (ViewModel.HasExecutionResult && ViewModel.CanOpenSingleOutput)
            {
                _ = ViewModel.OpenSingleOutputAsync();
            }
            else if (ViewModel.HasExecutionResult)
            {
                _ = ViewModel.RevealOutputsAsync();
            }
            return;
        }

        if ((int)e.Key == 188 /* VK_OEM_COMMA */ &&
            Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
        {
            e.Handled = true;
            OpenSettingsFromPalette();
            return;
        }

        if (!actionInput || ViewModel.ShowFirstRun) return;

        bool modified = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)
            || Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Menu).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (modified) return;

        if (e.Key == Windows.System.VirtualKey.Up)
        {
            e.Handled = true;
            ViewModel.MoveSelectionUp();
            return;
        }

        if (e.Key == Windows.System.VirtualKey.Down)
        {
            e.Handled = true;
            ViewModel.MoveSelectionDown();
            return;
        }

        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            var outcome = await _enterController.HandleEnterAsync(ViewModel, ExecuteActionAsync,
                () => AppWindow.IsVisible && !_dialogOpen);
            if (outcome == PaletteEnterOutcome.Retried)
                SearchBox.Focus(FocusState.Keyboard);
        }
    }

    private static bool IsWithin(DependencyObject? element, DependencyObject ancestor)
    {
        while (element != null)
        {
            if (ReferenceEquals(element, ancestor)) return true;
            element = VisualTreeHelper.GetParent(element);
        }
        return false;
    }

    private async void ActionList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (!((App)App.Current).UpdateInstalling && e.ClickedItem is ActionSearchResultProxy proxy && !ViewModel.IsExecuting && !_dialogOpen && !_enterController.IsPending)
        {
            if (proxy.IsCompatible)
            {
                await ExecuteActionAsync(proxy.Original);
            }
        }
    }

    private async System.Threading.Tasks.Task ExecuteActionAsync(ActionSearchResult result)
    {
        if (((App)App.Current).UpdateInstalling || ViewModel.IsExecuting || _dialogOpen)
            return;

        var app = App.Current as App;
        var descriptor = result.Action.Descriptor;
        if (descriptor.Id == PowerTrove.Application.Commands.OpenSettingsAction.ActionId)
        {
            OpenSettingsFromPalette();
            return;
        }
        var expectedSnapshot = ViewModel.CurrentSnapshot;
        if (result.Action is CatalogAction missing)
        {
            if (app?.PackManagement is null || app.Actions is null || expectedSnapshot is null) return;
            using var cancel = new CancellationTokenSource();
            _packInstallCancellation = cancel; _dialogOpen = true;
            try
            {
                using var controller = new PackOnDemandController(app.PackManagement, app.Actions);
                var resolved = await controller.ResolveAsync(missing, expectedSnapshot, ConfirmPackInstallAsync,
                    () => AppWindow.IsVisible && ReferenceEquals(ViewModel.CurrentSnapshot, expectedSnapshot),
                    (offer, token) => InstallPackWithProgressAsync(app.PackManagement, offer, token), cancel.Token);
                if (resolved.Outcome != PackOnDemandOutcome.Ready || resolved.Action is null)
                {
                    if (resolved.Message is not null) ViewModel.ShowPackNotice(resolved.Message);
                    if (app.Actions.GetAction(missing.Descriptor.Id) is not CatalogAction && ReferenceEquals(ViewModel.CurrentSnapshot, expectedSnapshot))
                        await ViewModel.RefreshActionsAsync();
                    return;
                }
                await ViewModel.RefreshActionsAsync();
                if (!ReferenceEquals(ViewModel.CurrentSnapshot, expectedSnapshot) || expectedSnapshot.IsStale || !AppWindow.IsVisible)
                { ViewModel.ShowPackNotice(UiText.PackSelectAgain); return; }
                result = result with { Action = resolved.Action, InstallState = PackInstallState.Installed,
                    IsCompatible = true, IncompatibilityReason = null, AvailabilityReason = null };
                descriptor = result.Action.Descriptor;
            }
            finally { _packInstallCancellation = null; _dialogOpen = false; if (AppWindow.IsVisible) SearchBox.Focus(FocusState.Programmatic); }
        }
        if (result.Action is not CatalogAction && descriptor.Id == PowerTrove.Core.Activation.ActivationProtocol.OrganizePagesActionId)
        {
            OpenPageOrganizer();
            return;
        }

        ViewModel.SelectedAction = result;
        if (expectedSnapshot is null || !AppWindow.IsVisible || !ReferenceEquals(ViewModel.CurrentSnapshot, expectedSnapshot) || expectedSnapshot.IsStale
            || !ReferenceEquals(app?.Actions?.GetAction(descriptor.Id), result.Action))
        { ViewModel.ShowPackNotice(UiText.PackSelectAgain); return; }
        if (result.InstallState != PackInstallState.NotInstalled)
            app?.SettingsService?.RecordActionExecution(descriptor.Id);

        // Saved preferences seed the options; file-changing actions always show the
        // options/preview dialog so nothing is written without confirmation.
        ActionParameters parameters = new EmptyParameters();
        var settingsService = app?.SettingsService;
        Dictionary<string, string>? prefs = null;
        settingsService?.Settings.ActionPreferences.TryGetValue(descriptor.Id, out prefs);
        if (prefs != null)
        {
            parameters = new DictionaryParameters(prefs);
        }

        if (descriptor.RequiresPreview || descriptor.ChangesFiles || descriptor.Parameters is { Count: > 0 })
        {
            ActionOptionsResult? options;
            _dialogOpen = true;
            try
            {
                IReadOnlyDictionary<string, string>? draft = null;
                while (true)
                {
                    options = await ActionOptionsDialog.ShowAsync(
                        this,
                        descriptor,
                        prefs,
                        (p, ct) => ViewModel.PreviewSelectedActionAsync(p, ct, expectedSnapshot, result.Action),
                        result.InstallState != PackInstallState.NotInstalled && VisualPageActions.Contains(descriptor.Id) ? OpenPageOrganizer : null,
                        allowMediaEditor: descriptor.Id == "powertrove.media.trim" && MediaTrimRange.CanEdit(expectedSnapshot),
                        initialValues: draft);
                    if (options is null || !options.EditMediaVisually) break;
                    if (!ReferenceEquals(ViewModel.CurrentSnapshot, expectedSnapshot) || expectedSnapshot.IsStale ||
                        !ReferenceEquals(app?.Actions?.GetAction(descriptor.Id), result.Action))
                    { ViewModel.ShowPackNotice(UiText.PackSelectAgain); options = null; break; }
                    var editor = new MediaTrimWindow(expectedSnapshot, options.Parameters,
                        (p, ct) => ViewModel.PreviewSelectedActionAsync(p, ct, expectedSnapshot, result.Action),
                        () => ReferenceEquals(ViewModel.CurrentSnapshot, expectedSnapshot) && !expectedSnapshot.IsStale &&
                            ReferenceEquals(ViewModel.SelectedAction?.Action, result.Action) &&
                            ReferenceEquals(app?.Actions?.GetAction(descriptor.Id), result.Action));
                    app?.ApplyWindowIcon(editor);
                    DictionaryParameters? chosen = null;
                    bool completed = false;
                    _activeVisualEditor = editor;
                    // DismissPalette clears the invocation; a visual handoff must retain it.
                    HidePalette();
                    try
                    {
                        chosen = await editor.ShowSelectionAsync();
                        completed = true;
                    }
                    finally
                    {
                        _activeVisualEditor = null;
                        if (!AllowClose && (!completed || chosen is not null || editor.ReturnToOptions)) ShowPalette();
                    }
                    if (AllowClose) { options = null; break; }
                    if (chosen is not null) { options = options with { Parameters = chosen, EditMediaVisually = false }; break; }
                    if (!editor.ReturnToOptions)
                    {
                        app?.DismissPalette(); options = null; break;
                    }
                    draft = editor.DraftParameters.Values;
                }
            }
            finally
            {
                _dialogOpen = false;
            }
            if (options == null)
            {
                if (!AllowClose && AppWindow.IsVisible) SearchBox.Focus(FocusState.Programmatic);
                return;
            }

            parameters = options.Parameters;
            if (options.Remember && settingsService != null)
            {
                foreach (var (key, value) in options.Parameters.Values)
                    settingsService.SetActionPreference(descriptor, key, value);
            }
        }

        if (expectedSnapshot is null || !AppWindow.IsVisible || !ReferenceEquals(ViewModel.CurrentSnapshot, expectedSnapshot) || expectedSnapshot.IsStale
            || !ReferenceEquals(app?.Actions?.GetAction(descriptor.Id), result.Action) || !ReferenceEquals(ViewModel.SelectedAction?.Action, result.Action))
        { ViewModel.ShowPackNotice(UiText.PackSelectAgain); return; }
        await ViewModel.ExecuteSelectedActionAsync(parameters);

        if (ViewModel.HasExecutionResult)
        {
            var historyItem = new PowerTrove.Application.Configuration.ActionHistoryItem(
                descriptor.Id,
                DateTimeOffset.UtcNow,
                ViewModel.ExecutionIsSuccess,
                ViewModel.CurrentOutputPaths?.Count ?? 0,
                ViewModel.ExecutionIsSuccess ? 0 : 1
            );
            app?.SettingsService?.AddActionHistory(historyItem);
        }

        // Dismissal logic is now handled by ViewModel.DismissRequested
    }

    private async Task<bool> ConfirmPackInstallAsync(PackConsentDetails details, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var dialog = new ContentDialog { Title = UiText.PackNeedsInstall(details.Name),
            Content = UiText.PackDownloadConsent(UiText.PackNameVersion(details.Name, details.Version), ActionPackRow.FormatSize(details.DownloadBytes), details.Publisher),
            PrimaryButtonText = UiText.PackInstallAndRun, CloseButtonText = UiText.CommonCancel, DefaultButton = ContentDialogButton.Close, XamlRoot = Content.XamlRoot };
        using var registration = token.Register(() => DispatcherQueue.TryEnqueue(() => dialog.Hide()));
        var result = await dialog.ShowAsync(); token.ThrowIfCancellationRequested();
        return result == ContentDialogResult.Primary;
    }

    private async Task<PackChannelStatus> InstallPackWithProgressAsync(IPackManagementService management, PackInstallOffer offer, CancellationToken token)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
        var text = new TextBlock { Text = UiText.PacksInstalling, TextWrapping = TextWrapping.Wrap };
        var bar = new ProgressBar { IsIndeterminate = offer.DownloadBytes <= 0, Maximum = Math.Max(1, offer.DownloadBytes) };
        var panel = new StackPanel { Spacing = 12 }; panel.Children.Add(text); panel.Children.Add(bar);
        var dialog = new ContentDialog { Title = offer.Name, Content = panel, CloseButtonText = UiText.CommonCancel, XamlRoot = Content.XamlRoot };
        bool ended = false;
        var progress = new Progress<PackDownloadProgress>(value =>
        {
            if (ended) return;
            text.Text = string.Format(CultureInfo.CurrentCulture, PowerTrove.Application.Resources.Strings.GetString("PackProgressBytes"), value.CompletedBytes);
            if (offer.DownloadBytes > 0) bar.Value = Math.Min(value.CompletedBytes, offer.DownloadBytes);
        });
        async Task<ContentDialogResult> ShowAsync() => await dialog.ShowAsync();
        var shown = ShowAsync();
        var install = management.InstallAsync(offer, progress, cancel.Token);
        try
        {
            bool canceledByDialog = await Task.WhenAny(shown, install) == shown;
            if (canceledByDialog) cancel.Cancel();
            var result = await install;
            // A late cancel may leave a successfully installed pack, but must not open action options.
            if (canceledByDialog) throw new OperationCanceledException(token);
            return result;
        }
        finally
        {
            ended = true; dialog.Hide();
            try { await shown; } catch (Exception) { }
        }
    }
}
