using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using PowerTrove.Core.Input;
using PowerTrove.Core;
using PowerTrove.Core.Search;
using PowerTrove.Platform.Windows.Capture;
using PowerTrove.Platform.Windows.Input;
using PowerTrove.Application;
using PowerTrove.Application.Configuration;
using PowerTrove.Application.Localization;
using PowerTrove.Application.ViewModels;
using PowerTrove.Application.Updates;
using PowerTrove.Core.Activation;
using PowerTrove.Platform.Windows.Activation;

namespace PowerTrove.Desktop;

using System.IO;
using System.Linq;

public partial class App : global::Microsoft.UI.Xaml.Application
{
    private GitHubReleaseDiscovery? _releaseDiscovery;
    private GitHubReleaseInstaller? _releaseInstaller;
    public ReleaseUpdatesViewModel Updates { get; private set; } = null!;
    public bool UpdateInstalling => Updates?.IsInstalling == true;
    public IReleaseDiscovery ReleaseDiscovery => _releaseDiscovery ??= GitHubReleaseDiscovery.CreateDefault();
    private MainWindow? m_window;
    private IHotkeyService? _hotkeyService;
    private InvocationCoordinator? _invocationCoordinator;
    private PowerTrove.Platform.Windows.Shell.TrayIcon? _trayIcon;
    private IntPtr _iconHandle;
    private SettingsWindow? _settingsWindow;
    private readonly IHostElector? _hostElector;
    private readonly ActivationMessage? _startupMessage;
    private readonly CancellationTokenSource _activationLifetime = new();
    private Task? _activationListener;
    private PackRegistryBinding? _packRegistryBinding;
    private readonly HashSet<Window> _editors = new();

    public ISettingsService? SettingsService { get; private set; }
    public IActionSearchEngine? SearchEngine { get; private set; }

    /// <summary>All registered actions (built-in packs and app commands).</summary>
    public IActionRegistry? Actions { get; private set; }
    public PowerTrove.Application.Packs.PackHostSupervisor? PackHosts { get; private set; }
    public PowerTrove.Application.Packs.PackInstaller? PackInstaller { get; private set; }
    public PowerTrove.Application.Packs.InstalledPackRuntime? PackRuntime { get; private set; }
    public PowerTrove.Application.Packs.PackChannelClient? PackChannel { get; private set; }
    public PowerTrove.Application.Packs.IPackManagementService? PackManagement { get; private set; }
    public void RefreshPackVisibility() => m_window?.ViewModel.RefreshPackVisibility();

    /// <summary>The language setting the UI was started with; a change applies after a restart.</summary>
    public string? StartupUiLanguage { get; private set; }

    public App()
    {
        InitializeComponent();
    }

    public App(IHostElector elector, ActivationMessage message) : this()
    {
        _hostElector = elector ?? throw new ArgumentNullException(nameof(elector));
        _startupMessage = message ?? throw new ArgumentNullException(nameof(message));
    }

    protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        // Settings first: the UI language must be applied before any window or pack reads resources.
        string appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string settingsFilePath = Path.Combine(appDataPath, "PowerTrove", "settings.json");
        SettingsService = new SettingsService(settingsFilePath);
        StartupUiLanguage = SettingsService.Settings.UiLanguage;
        UiLanguages.Apply(UiLanguages.Resolve(StartupUiLanguage, System.Globalization.CultureInfo.CurrentUICulture));
        string workerExecutable = Path.Combine(AppContext.BaseDirectory, "packhost", "PowerTrove.PackHost.exe");
        if (!File.Exists(workerExecutable))
            workerExecutable = Path.Combine(AppContext.BaseDirectory, "PowerTrove.PackHost.exe");
        PackHosts = new PowerTrove.Application.Packs.PackHostSupervisor(
            new PowerTrove.Platform.Windows.Packs.WindowsPackWorkerProcessFactory(workerExecutable),
            Path.Combine(appDataPath, "PowerTrove"));
        PackInstaller = new PowerTrove.Application.Packs.PackInstaller(Path.Combine(appDataPath, "PowerTrove"),
            PowerTrove.Application.Packs.PackDistributionConfiguration.HostVersion, PackHosts.StopPackAsync);
        PackChannel = PowerTrove.Application.Packs.PackChannelClient.CreateDefault(PackInstaller, Path.Combine(appDataPath, "PowerTrove"), PowerTrove.Application.Packs.PackDistributionConfiguration.HostVersion);

        _releaseInstaller = GitHubReleaseInstaller.CreateDefault(
            new PowerTrove.Platform.Windows.Shell.ReleaseInstallerLauncher(),
            Path.Combine(appDataPath, "PowerTrove", "updates"), () => m_window?.CanCloseForUpdate == true && _settingsWindow?.CanCloseForUpdate != false && _editors.Count == 0);
        _ = Task.Run(_releaseInstaller.CleanupStaleDownloads);
        string? version = typeof(App).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion
            ?? typeof(App).Assembly.GetName().Version?.ToString();
        Updates = new ReleaseUpdatesViewModel(version, ReleaseDiscovery,
            new PowerTrove.Platform.Windows.Shell.ReleasePageLauncher(), _releaseInstaller);
        Updates.InstallerStarted += (_, _) => ExitApplication();
        await PackChannel.LoadCachedAsync(_activationLifetime.Token);
        m_window = new MainWindow();

        var foregroundCapture = new WindowsForegroundCapture();

        var providers = BuiltInContextProviders.Create();
        var contextResolver = new ContextResolver(providers);

        var actionRegistry = new ActionRegistry();
        var workflowRunner = new PowerTrove.Core.Workflows.WorkflowRunner(actionRegistry);

        var undoService = new PowerTrove.Core.UndoService(new PowerTrove.Abstractions.Files.PhysicalOutputFileSystem());
        m_window.ViewModel.UndoService = undoService;
        var installedPacks = BuiltInActionPacks.Create(SettingsService.Settings.ImageExportPresets, workflowRunner, undoService).ToArray();
        bool isolationUnavailable = false;
        if (SettingsService.Settings.RunFileToolsInSeparateProcess)
        {
            var presetIds = (SettingsService.Settings.ImageExportPresets ?? [])
                .Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
            var isolation = await PowerTrove.Application.Packs.BuiltInPackIsolation.RouteAsync(
                installedPacks, PackHosts, AppContext.BaseDirectory, presetIds);
            installedPacks = isolation.Packs.ToArray();
            isolationUnavailable = !isolation.Ready;
        }
        foreach (var pack in installedPacks)
        {
            actionRegistry.RegisterPack(pack.Info.Id, pack.Actions);
        }
        actionRegistry.Register(new PowerTrove.Application.Commands.OpenSettingsAction(() => OpenSettings()));
        var catalog = await ActionCatalogFile.LoadAsync(Path.Combine(AppContext.BaseDirectory, "catalog.json"),
            installedPacks.Select(p => p.Info), PowerTrove.Application.Packs.PackDistributionConfiguration.HostVersion);
        m_window.ViewModel.CatalogWarningMessage = isolationUnavailable ? UiText.IsolationUnavailable : catalog.Warning;
        try
        {
            actionRegistry.RegisterCatalog(catalog.Catalog);
        }
        catch (InvalidOperationException)
        {
            m_window.ViewModel.CatalogWarningMessage = PowerTrove.Core.Resources.Strings.CatalogLoadFailed;
        }
        Actions = actionRegistry;
        PackRuntime = new PowerTrove.Application.Packs.InstalledPackRuntime(PackInstaller, PackHosts, actionRegistry, installedPacks, catalog.Catalog);
        try { await PackRuntime.ReloadAsync(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException)
        { m_window.ViewModel.CatalogWarningMessage = PowerTrove.Core.Resources.Strings.CatalogLoadFailed; }
        var packManagement = new PowerTrove.Application.Packs.PackManagementService(PackInstaller, PackChannel, PackRuntime, installedPacks, catalog.Catalog);
        try { await packManagement.InitializeAsync(_activationLifetime.Token); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
        { m_window.ViewModel.CatalogWarningMessage = PowerTrove.Core.Resources.Strings.CatalogLoadFailed; }
        PackManagement = new PowerTrove.Application.Packs.CoordinatedPackManagement(packManagement, PackHosts.AcquireMutationAsync);

        var aliasesPath = Path.Combine(AppContext.BaseDirectory, "aliases.json");
        var aliases = ActionAliasLoader.LoadAliases(aliasesPath);

        var usageSignals = new PowerTrove.Application.Search.SelectionUsageSignals(SettingsService);

        SearchEngine = new ActionSearchEngine(
            actionRegistry,
            usageSignals,
            aliases,
            SettingsService.Settings.PinnedActions,
            SettingsService.Settings.RecentActions);

        _invocationCoordinator = new InvocationCoordinator(
            SettingsService,
            foregroundCapture,
            contextResolver,
            m_window.ViewModel,
            actionRegistry,
            SearchEngine);

        var monitorService = new PowerTrove.Platform.Windows.Displays.WindowsMonitorService();

        _invocationCoordinator.PaletteRequested += (s, hwnd) =>
        {
            if (m_window != null)
            {
                if (hwnd is { } sourceHwnd)
                    m_window.PositionPalette(sourceHwnd, monitorService);
                m_window.ShowPalette();
            }
        };

        _invocationCoordinator.ResurfaceRequested += (s, e) =>
        {
            if (m_window != null)
            {
                m_window.ShowPalette();
            }
        };

        // Initialize Hotkey Service
        _hotkeyService = new WindowsHotkeyService();

        _hotkeyService.HotkeyTriggered += OnHotkeyTriggered;

        var config = SettingsService.Settings.GlobalShortcut ?? new HotkeyConfiguration(KeyModifiers.Alt, 0x20 /* VK_SPACE */);
        var result = _hotkeyService.Register(config);

        if (result == RegisterHotkeyResult.Conflict)
        {
            m_window.ViewModel.HotkeyWarningMessage = UiText.HotkeyConflict;
        }
        else if (result != RegisterHotkeyResult.Success)
        {
            m_window.ViewModel.HotkeyWarningMessage = UiText.HotkeyRegistrationFailed;
        }

        m_window.ViewModel.ShowFirstRun = !SettingsService.Settings.HasSeenFirstRun;

        var emptySnapshot = new ContextSnapshot(
            Guid.NewGuid(),
            new SourceWindowIdentity(IntPtr.Zero, 0, null, DateTimeOffset.UtcNow),
            ContextStatus.Empty,
            new NoContent(),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(5),
            "startup",
            UiText.StatusReady);
        var shellNavigator = new PowerTrove.Platform.Windows.Shell.ShellNavigator();
        var outputRevealer = new OutputRevealer(shellNavigator);
        m_window.ViewModel.OutputRevealer = outputRevealer;
        m_window.ViewModel.ClipboardWriter = new DispatchingClipboardWriter(
            new PowerTrove.Platform.Windows.ClipboardWriter(), m_window.DispatcherQueue);
        m_window.ViewModel.ContextPacks.Configure(PackManagement!);
        _packRegistryBinding = new PackRegistryBinding(actionRegistry, m_window.ViewModel,
            callback => m_window?.DispatcherQueue.TryEnqueue(() => callback()) == true);
        m_window.ViewModel.SettingsService = SettingsService;
        m_window.ViewModel.Initialize(emptySnapshot, actionRegistry, SearchEngine, SettingsService);

        CreateIconAndTray();
        if (_hostElector is not null)
            _activationListener = _hostElector.StartListeningAsync(ReceiveActivationAsync, _activationLifetime.Token);
        _ = Updates.CheckOnStartupAsync(SettingsService.Settings.CheckForUpdatesOnStartup, _activationLifetime.Token);
        if (_startupMessage is ExplicitSelectionMessage selection)
        {
            // Invalid explicit commands must not silently capture another window or open a generic palette.
            if (!TryShowExplicitSelection(selection)) ExitApplication();
            return;
        }
        m_window.PositionPalette(IntPtr.Zero, monitorService);
        m_window.ShowPalette();
    }

    private Task<bool> ReceiveActivationAsync(ActivationMessage message, CancellationToken token)
    {
        if (UpdateInstalling || token.IsCancellationRequested || m_window is null) return Task.FromResult(false);
        ContextSnapshot? snapshot = null;
        if (message is ExplicitSelectionMessage selection)
        {
            snapshot = new ExplicitSelectionValidator().CreateSnapshot(selection, token);
            if (snapshot is null || (selection.ActionId is not null &&
                selection.ActionId != ActivationProtocol.OrganizePagesActionId))
                return Task.FromResult(false);
        }
        else if (message is not ActivatePaletteMessage) return Task.FromResult(false);

        if (message is ExplicitSelectionMessage { ActionId: not null } &&
            (Actions?.GetAction(PowerTrove.Pack.Pdf.OrganizePagesPdfAction.ActionId) is null or PowerTrove.Core.Catalog.CatalogAction))
            return Task.FromResult(false);
        var window = m_window;
        bool queued = window.DispatcherQueue.TryEnqueue(() =>
        {
            if (_activationLifetime.IsCancellationRequested || m_window is null) return;
            if (message is ExplicitSelectionMessage explicitMessage && snapshot is not null)
                ShowExplicitSelection(explicitMessage, snapshot);
            else
                ShowGenericActivation();
        });
        return Task.FromResult(queued);
    }

    private void ShowGenericActivation()
    {
        if (UpdateInstalling) return;
        if (m_window?.TryActivateVisualEditor() == true) return;
        if (m_window is null || m_window.ViewModel.IsExecuting || Actions is null || SearchEngine is null || SettingsService is null) return;
        _invocationCoordinator?.Dismiss();
        var now = DateTimeOffset.UtcNow;
        m_window.ViewModel.Initialize(new ContextSnapshot(Guid.NewGuid(),
            new SourceWindowIdentity(IntPtr.Zero, 0, null, now), ContextStatus.Empty,
            new NoContent(), now, now.AddMinutes(5), "activation", UiText.StatusReady),
            Actions, SearchEngine, SettingsService);
        m_window.ShowPalette();
    }

    private bool TryShowExplicitSelection(ExplicitSelectionMessage selection)
    {
        var snapshot = new ExplicitSelectionValidator().CreateSnapshot(selection);
        return snapshot is not null && ShowExplicitSelection(selection, snapshot);
    }

    private bool ShowExplicitSelection(ExplicitSelectionMessage selection, ContextSnapshot snapshot)
    {
        if (UpdateInstalling || m_window?.TryActivateVisualEditor() == true) return false;
        if (m_window is null || m_window.ViewModel.IsExecuting) return false;
        if (selection.ActionId is null)
        {
            if (Actions is null || SearchEngine is null || SettingsService is null) return false;
            _invocationCoordinator?.Dismiss();
            m_window.ViewModel.Initialize(snapshot, Actions, SearchEngine, SettingsService);
            m_window.ShowPalette();
            return true;
        }
        if (selection.ActionId != ActivationProtocol.OrganizePagesActionId) return false;
        var action = Actions?.GetAction(PowerTrove.Pack.Pdf.OrganizePagesPdfAction.ActionId);
        if (action is null || action is PowerTrove.Core.Catalog.CatalogAction) return false;
        var organizer = new PageOrganizerWindow(action, snapshot);
        TrackEditor(organizer);
        ApplyWindowIcon(organizer);
        organizer.Activate();
        return true;
    }

    private void CreateIconAndTray()
    {
        _iconHandle = PowerTrove.Platform.Windows.Shell.AppIcon.CreateHandle(32);
        if (_iconHandle == IntPtr.Zero || m_window == null)
            return;
        m_window.AppWindow.SetIcon(Microsoft.UI.Win32Interop.GetIconIdFromIcon(_iconHandle));
        try
        {
            _trayIcon = new PowerTrove.Platform.Windows.Shell.TrayIcon(
                _iconHandle, UiText.TrayTooltip, UiText.TrayOpen, UiText.TraySettings, UiText.TrayExit);
            _trayIcon.OpenRequested += (_, _) => RequestPaletteOrEditor();
            _trayIcon.SettingsRequested += (_, _) => OpenSettings();
            _trayIcon.ExitRequested += (_, _) => ExitApplication();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The tray icon is a convenience; the hotkey and palette keep working without it.
            _trayIcon = null;
        }
    }

    /// <summary>Closes all windows, removes the tray icon and ends the process.</summary>
    public async void ExitApplication()
    {
        _activationLifetime.Cancel();
        _packRegistryBinding?.Dispose();
        Updates?.Dispose();
        _releaseInstaller?.Dispose();
        _settingsWindow?.Close();
        if (m_window is { } window)
        {
            window.AllowClose = true;
            window.Close();
        }
        DisposeHotkeyService();
        _releaseDiscovery?.Dispose();
        _releaseDiscovery = null;
        (PackManagement as IDisposable)?.Dispose();
        PackChannel?.Dispose();
        PackChannel = null;
        if (PackHosts is { } hosts)
        {
            PackHosts = null;
            await hosts.DisposeAsync();
        }
        if (_activationListener is not null)
        {
            try { await _activationListener; }
            catch (OperationCanceledException) { }
            catch (IOException) { }
        }
        _hostElector?.Dispose();
        _activationLifetime.Dispose();
        Exit();
    }

    public bool TryUpdateGlobalShortcut(HotkeyConfiguration config, out string errorMessage)
    {
        errorMessage = string.Empty;
        if (_hotkeyService == null || SettingsService == null) return false;

        var result = _hotkeyService.Register(config);

        if (result == RegisterHotkeyResult.Success)
        {
            SettingsService.SetGlobalShortcut(config);
            if (m_window != null) m_window.ViewModel.HotkeyWarningMessage = "";
            return true;
        }

        if (result == RegisterHotkeyResult.Conflict)
        {
            errorMessage = UiText.HotkeyConflict;
            if (m_window != null) m_window.ViewModel.HotkeyWarningMessage = errorMessage;
            return false;
        }

        errorMessage = UiText.HotkeyRegistrationFailed;
        if (m_window != null) m_window.ViewModel.HotkeyWarningMessage = errorMessage;
        return false;
    }

    /// <summary>Keep open editors safe from update shutdown.</summary>
    public void TrackEditor(Window window)
    {
        _editors.Add(window);
        window.Closed += (_, _) => _editors.Remove(window);
    }

    /// <summary>Gives a secondary window the PowerTrove icon.</summary>
    public void ApplyWindowIcon(Window window)
    {
        if (_iconHandle != IntPtr.Zero)
            window.AppWindow.SetIcon(Microsoft.UI.Win32Interop.GetIconIdFromIcon(_iconHandle));
    }

    public void OpenSettings()
    {
        if (_settingsWindow == null)
        {
            _settingsWindow = new SettingsWindow();
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            if (_iconHandle != IntPtr.Zero)
                _settingsWindow.AppWindow.SetIcon(Microsoft.UI.Win32Interop.GetIconIdFromIcon(_iconHandle));
        }
        _settingsWindow.Activate();
    }

    private void OnHotkeyTriggered(object? sender, EventArgs e)
    {
        RequestPaletteOrEditor();
    }

    private void RequestPaletteOrEditor()
    {
        if (UpdateInstalling) return;
        if (m_window?.TryActivateVisualEditor() == true) return;
        _ = _invocationCoordinator?.InvokeAsync();
    }

    public void DismissPalette()
    {
        _invocationCoordinator?.Dismiss();
        m_window?.HidePalette();
    }

    public void DisposeHotkeyService()
    {
        _invocationCoordinator?.Dismiss();
        m_window = null;
        _trayIcon?.Dispose();
        _trayIcon = null;
        if (_hotkeyService != null)
        {
            _hotkeyService.HotkeyTriggered -= OnHotkeyTriggered;
            _hotkeyService.Dispose();
            _hotkeyService = null;
        }
    }
}
