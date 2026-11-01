using System;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using PowerTrove.Application.Localization;
using PowerTrove.Application.Configuration;
using PowerTrove.Core.Input;
using PowerTrove.Platform.Windows.Media;
using PowerTrove.Platform.Windows.Shell;
using PowerTrove.Application.ViewModels;

namespace PowerTrove.Desktop;

public sealed partial class SettingsWindow : HostedView
{
    private bool _loadingLanguage;
    private bool _loadingIsolation = true;
    private readonly IExplorerMenuRegistration _explorerMenu = new ExplorerMenuRegistration();
    private readonly IStartupRegistration _startup = new StartupRegistration();
    private bool _startupBusy;
    private bool _showingStartupState;
    private bool _explorerBusy;
    private readonly IFFmpegSetup _ffmpeg = new FFmpegSetup();
    private System.Threading.CancellationTokenSource? _ffmpegInstall;
    private bool _ffmpegChecking;
    private System.Threading.CancellationTokenSource? _packInstallCancellation;
    private bool _packsClosed;
    private bool _packDialogOpen;
    public ActionPacksViewModel? Packs { get; }
    // Settings hold no edits of their own; only running pack, Explorer or FFmpeg work keeps them on screen.
    public override bool KeepsWindow => !CanCloseForUpdate;
    // Start on the first setting, never on a footer button that Enter would trigger.
    protected internal override Control? InitialFocus => LanguageComboBox;
    public override bool CanCloseForUpdate => !_packDialogOpen && _packInstallCancellation is null && !_explorerBusy && !_startupBusy && _ffmpegInstall is null && Packs?.IsBusy != true;
    private bool _loadingUpdatePreference = true;
    private bool _loadingPackUpdatePreference = true;
    public ReleaseUpdatesViewModel Updates { get; }

    private bool IsPackaged => ((App)App.Current).Distribution.IsPackaged;
    public string ExplorerDescription => IsPackaged ? UiText.StoreExplorerDescription : UiText.ExplorerDescription;
    public string FFmpegDescription => IsPackaged ? UiText.StoreFFmpegDescription : UiText.FFmpegDescription;

    public SettingsWindow(bool selectPacks = false)
    {
        Updates = ((App)App.Current).Updates;
        if (App.Current is App { PackManagement: { } management, SettingsService: { } settings } owner)
            Packs = new ActionPacksViewModel(management, settings, owner.RefreshPackVisibility);
        this.InitializeComponent();
        Closed += (_, _) => { _packsClosed = true; _packInstallCancellation?.Cancel(); _ffmpegInstall?.Cancel(); Packs?.Dispose(); };
        Title = UiText.SettingsWindowTitle;
        TitleBar = AppTitleBar;
        TitleBarIcon.Source = AppIconImage.Create(32);
        AboutIcon.Source = AppIconImage.Create(64);
        PreferredSize = new(820, 760);
        if (IsPackaged)
        {
            ExplorerAddButton.Visibility = ExplorerRemoveButton.Visibility = Visibility.Collapsed;
            FFmpegInstallButton.Visibility = FFmpegCancelButton.Visibility = Visibility.Collapsed;
            GitHubUpdatesCard.Visibility = Visibility.Collapsed;
            StoreUpdatesCard.Visibility = Visibility.Visible;
            StartupCard.Visibility = Visibility.Collapsed;
        }
        _ = RefreshExplorerRegistrationAsync();
        _ = ChangeStartupAsync(enable: null);
        _ = RefreshFFmpegAsync();
        if (selectPacks) PackSelectionExpander.IsExpanded = true;

        if (App.Current is App app && app.SettingsService != null)
        {
            StartupUpdatesToggle.IsOn = app.SettingsService.Settings.CheckForUpdatesOnStartup;
            _loadingUpdatePreference = false;
            AutomaticPackUpdatesToggle.IsOn = app.SettingsService.Settings.CheckForPackUpdatesAutomatically;
            AutomaticPackUpdatesToggle.IsEnabled = !app.SettingsService.IsReadOnly;
            _loadingPackUpdatePreference = false;
            LoadLanguageChoices(app.SettingsService.Settings.UiLanguage);
            SeparateProcessToggle.IsOn = app.SettingsService.Settings.RunFileToolsInSeparateProcess;
            _loadingIsolation = false;
            var config = app.SettingsService.Settings.GlobalShortcut ?? HotkeyAdvisor.Default;
            UpdateShortcutText(config);
            ShowShortcutSuggestion(app);
            LoadRecipes();

            if (app.SettingsService.IsReadOnly)
            {
                ReadOnlyWarning.Visibility = Visibility.Visible;
                MainSettingsPanel.IsEnabled = false;
            }
        }
    }

    private async void OpenStoreUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (!IsPackaged) return;
        try
        {
            if (!await Launcher.LaunchUriAsync(new Uri("ms-windows-store://downloadsandupdates")))
                StoreUpdatesStatus.Text = UiText.StoreUpdatesOpenFailed;
        }
        catch (Exception) { StoreUpdatesStatus.Text = UiText.StoreUpdatesOpenFailed; }
    }

    private void SeparateProcessToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingIsolation) return;
        if (App.Current is App { SettingsService: { } service })
        {
            service.SetRunFileToolsInSeparateProcess(SeparateProcessToggle.IsOn);
            SeparateProcessRestartMessage.Visibility = Visibility.Visible;
        }
    }

    private void LoadLanguageChoices(string? current)
    {
        _loadingLanguage = true;
        // Language names are shown in their own language, so users can always find theirs.
        var choices = new (string? Code, string Text)[]
        {
            (null, UiText.SettingsLanguageSystem),
            (UiLanguages.English, UiText.SettingsLanguageEnglish),
            (UiLanguages.German, UiText.SettingsLanguageGerman),
        };
        foreach (var (code, text) in choices)
        {
            var item = new ComboBoxItem { Content = text, Tag = code ?? UiLanguages.System };
            LanguageComboBox.Items.Add(item);
            if (code == UiLanguages.Normalize(current))
                LanguageComboBox.SelectedItem = item;
        }
        _loadingLanguage = false;
    }

    private void LanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingLanguage || LanguageComboBox.SelectedItem is not ComboBoxItem { Tag: string code })
            return;
        if (App.Current is App app && app.SettingsService != null)
        {
            string? language = UiLanguages.Normalize(code);
            app.SettingsService.SetUiLanguage(language);
            LanguageRestartTextBlock.Visibility = language == app.StartupUiLanguage
                ? Visibility.Collapsed
                : Visibility.Visible;
        }
    }

    private void ShortcutTextBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        e.Handled = true;

        var key = e.Key;

        // Ignore modifier-only keys
        if (key == VirtualKey.Control || key == VirtualKey.LeftControl || key == VirtualKey.RightControl ||
            key == VirtualKey.Shift || key == VirtualKey.LeftShift || key == VirtualKey.RightShift ||
            key == VirtualKey.Menu || key == VirtualKey.LeftMenu || key == VirtualKey.RightMenu ||
            key == VirtualKey.LeftWindows || key == VirtualKey.RightWindows)
        {
            return;
        }

        KeyModifiers modifiers = KeyModifiers.None;

        // WinUI's InputKeyboardSource or GetKeyState is ideal here, but for simplicity in this example
        // we can check CoreWindow state. However, WinUI 3 Desktop doesn't have a reliable CoreWindow.
        // We'll use the native GetKeyState or rely on Microsoft.UI.Input.InputKeyboardSource.
        // Let's use Microsoft.UI.Input.InputKeyboardSource to read modifiers
        var stateControl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
        if (stateControl.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) modifiers |= KeyModifiers.Control;

        var stateShift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift);
        if (stateShift.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) modifiers |= KeyModifiers.Shift;

        var stateMenu = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu);
        if (stateMenu.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) modifiers |= KeyModifiers.Alt;

        var stateWin = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.LeftWindows);
        var stateWinRight = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.RightWindows);
        if (stateWin.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down) || stateWinRight.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) modifiers |= KeyModifiers.Windows;

        // If no modifiers, it's not a good global shortcut, but we'll let it through or require at least one.
        if (modifiers == KeyModifiers.None)
        {
            ShortcutErrorTextBlock.Text = UiText.SettingsShortcutNeedsModifier;
            ShortcutErrorTextBlock.Visibility = Visibility.Visible;
            return;
        }

        if (App.Current is App app) ApplyShortcut(app, new HotkeyConfiguration(modifiers, (uint)key));
    }

    private void ApplyShortcut(App app, HotkeyConfiguration config)
    {
        if (app.TryUpdateGlobalShortcut(config, out string error))
        {
            UpdateShortcutText(config);
            ShortcutErrorTextBlock.Visibility = Visibility.Collapsed;
        }
        else
        {
            ShortcutErrorTextBlock.Text = error;
            ShortcutErrorTextBlock.Visibility = Visibility.Visible;
        }
        ShowShortcutSuggestion(app);
    }

    /// <summary>Offers the free alternative the app found for a failed or shared shortcut.</summary>
    private void ShowShortcutSuggestion(App app)
    {
        if (app.SuggestedShortcut is { } suggestion)
        {
            ShortcutSuggestionButton.Content = UiText.HotkeyUseSuggestion(ShortcutText.Format(suggestion));
            ShortcutSuggestionButton.Visibility = Visibility.Visible;
        }
        else ShortcutSuggestionButton.Visibility = Visibility.Collapsed;
    }

    private void UseSuggestedShortcut_Click(object sender, RoutedEventArgs e)
    {
        if (App.Current is App { SuggestedShortcut: { } suggestion } app) ApplyShortcut(app, suggestion);
    }

    private void UpdateShortcutText(HotkeyConfiguration config) => ShortcutTextBox.Text = ShortcutText.Format(config);

    private void ResetShortcut_Click(object sender, RoutedEventArgs e)
    {
        if (App.Current is App app) ApplyShortcut(app, HotkeyAdvisor.Default);
    }

    private void CreateRecipe_Click(object sender, RoutedEventArgs e) => OpenRecipeEditor(null);

    private void OpenRecipeEditor(SavedRecipe? recipe)
    {
        // The editor replaces settings in the window, as settings replaced the palette.
        if (App.Current is App app) app.OpenRecipeEditor(recipe);
    }

    private void LoadRecipes()
    {
        if (!Features.UserRecipes) return;
        RecipesHeader.Visibility = RecipesCard.Visibility = Visibility.Visible;
        RecipeList.Children.Clear();
        if (App.Current is not App { SettingsService: IUserRecipeSettings settings } app) return;
        if (settings.UserRecipes.Count == 0)
        {
            RecipeList.Children.Add(new TextBlock { Text = UiText.SettingsRecipesEmpty, Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["SettingsDescriptionStyle"] });
            return;
        }
        foreach (var recipe in settings.UserRecipes)
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var steps = string.Join(" → ", recipe.Steps.Select(step => app.Actions?.GetAction(step.ActionId)?.Descriptor.Title ?? UiText.RecipeStepMissing));
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = recipe.Title, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock { Text = steps, TextWrapping = TextWrapping.Wrap, Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["SettingsDescriptionStyle"] });
            var edit = new Button { Content = UiText.RecipeEdit, VerticalAlignment = VerticalAlignment.Center };
            edit.Click += (_, _) => OpenRecipeEditor(recipe);
            var remove = new Button { Content = UiText.RecipeDelete, VerticalAlignment = VerticalAlignment.Center };
            remove.Click += async (_, _) => await DeleteRecipeAsync(recipe);
            AutomationProperties.SetName(edit, UiText.RecipeEditNamed(recipe.Title));
            AutomationProperties.SetName(remove, UiText.RecipeDeleteNamed(recipe.Title));
            Grid.SetColumn(edit, 1);
            Grid.SetColumn(remove, 2);
            row.Children.Add(text);
            row.Children.Add(edit);
            row.Children.Add(remove);
            RecipeList.Children.Add(row);
        }
    }

    private async System.Threading.Tasks.Task DeleteRecipeAsync(SavedRecipe recipe)
    {
        if (App.Current is not App { SettingsService: IUserRecipeSettings settings } app) return;
        var dialog = new ContentDialog
        {
            Title = UiText.RecipeDeleteTitle,
            Content = UiText.RecipeDeleteText(recipe.Title),
            PrimaryButtonText = UiText.RecipeDelete,
            CloseButtonText = UiText.CommonCancel,
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.Content.XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        settings.RemoveUserRecipe(recipe.Id);
        app.RefreshUserRecipes();
        LoadRecipes();
    }

    private async void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        if (App.Current is not App app) return;
        try
        {
            // Explicit user request: the only time PowerTrove writes these values to the clipboard.
            await new PowerTrove.Platform.Windows.ClipboardWriter().WriteTextAsync(app.BuildDiagnostics());
            DiagnosticsStatus.Text = UiText.SettingsDiagnosticsCopied;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            DiagnosticsStatus.Text = UiText.SettingsDiagnosticsCopyFailed;
        }
    }

    private void UnpinAction_Click(object sender, RoutedEventArgs e)
    {
        var actionId = UnpinActionTextBox.Text?.Trim();
        if (!string.IsNullOrEmpty(actionId) && App.Current is App app && app.SettingsService != null)
        {
            app.SettingsService.UnpinAction(actionId);
            UnpinActionStatusTextBlock.Text = UiText.SettingsUnpinDone;
            UnpinActionStatusTextBlock.Visibility = Visibility.Visible;
            UnpinActionTextBox.Text = "";
        }
    }

    private void ClearRecentActions_Click(object sender, RoutedEventArgs e)
    {
        if (App.Current is App app && app.SettingsService != null)
        {
            app.SettingsService.ClearRecentActions();
            ClearRecentStatusTextBlock.Text = UiText.SettingsRecentCleared;
            ClearRecentStatusTextBlock.Visibility = Visibility.Visible;
        }
    }

    private async void ClearSuggestionsHistory_Click(object sender, RoutedEventArgs e)
    {
        if (App.Current is App app && app.SettingsService != null)
        {
            var dialog = new ContentDialog
            {
                Title = UiText.SettingsClearSuggestionsTitle,
                Content = UiText.SettingsClearSuggestionsText,
                PrimaryButtonText = UiText.CommonClear,
                CloseButtonText = UiText.CommonCancel,
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.Content.XamlRoot
            };

            var result = await dialog.ShowAsync();

            if (result == ContentDialogResult.Primary)
            {
                app.SettingsService.ClearSelectionUsage();
                app.SearchEngine?.RefreshSignals();
                ClearSelectionHistoryStatusTextBlock.Text = UiText.SettingsSuggestionsCleared;
                ClearSelectionHistoryStatusTextBlock.Visibility = Visibility.Visible;
            }
        }
    }

    private void ResetToDefaults_Click(object sender, RoutedEventArgs e)
    {
        if (App.Current is App app && app.SettingsService != null)
        {
            app.SettingsService.ResetToDefaults();
            Packs?.RefreshPreference();
            StartupUpdatesToggle.IsOn = app.SettingsService.Settings.CheckForUpdatesOnStartup;
            AutomaticPackUpdatesToggle.IsOn = app.SettingsService.Settings.CheckForPackUpdatesAutomatically;
            app.PackUpdates.SetEnabled(app.SettingsService.Settings.CheckForPackUpdatesAutomatically);

            // Re-register default hotkey
            var config = HotkeyAdvisor.Default;
            app.TryUpdateGlobalShortcut(config, out _);
            UpdateShortcutText(config);
            ShowShortcutSuggestion(app);

            _loadingLanguage = true;
            LanguageComboBox.SelectedIndex = 0; // the defaults follow the Windows language
            _loadingLanguage = false;
            LanguageRestartTextBlock.Visibility = app.StartupUiLanguage == null ? Visibility.Collapsed : Visibility.Visible;

            ClearRecentStatusTextBlock.Text = UiText.SettingsResetDone;
            ClearRecentStatusTextBlock.Visibility = Visibility.Visible;
            ShortcutErrorTextBlock.Visibility = Visibility.Collapsed;
        }
    }

    private static string ExplorerExePath() => Environment.ProcessPath ??
        System.IO.Path.Combine(AppContext.BaseDirectory, "PowerTrove.exe");

    private static string ExplorerStateLabel(ExplorerMenuState state) => state switch
    {
        ExplorerMenuState.Registered => UiText.ExplorerRegistered,
        ExplorerMenuState.NotRegistered => UiText.ExplorerAbsent,
        ExplorerMenuState.Incomplete => UiText.ExplorerIncomplete,
        ExplorerMenuState.Conflict => UiText.ExplorerConflict,
        _ => UiText.ExplorerUnavailable
    };

    private async System.Threading.Tasks.Task RefreshExplorerRegistrationAsync()
    {
        if (IsPackaged) return;
        if (Updates.IsInstalling || _explorerBusy || _packsClosed) return;
        _explorerBusy = true;
        ExplorerAddButton.IsEnabled = ExplorerRemoveButton.IsEnabled = false;
        try
        {
            var state = await System.Threading.Tasks.Task.Run(() => _explorerMenu.GetState(ExplorerExePath()));
            if (!_packsClosed) ExplorerMenuStatus.Text = ExplorerStateLabel(state);
        }
        catch (Exception)
        {
            if (!_packsClosed) ExplorerMenuStatus.Text = UiText.ExplorerUnavailable;
        }
        finally
        {
            _explorerBusy = false;
            if (!_packsClosed) ExplorerAddButton.IsEnabled = ExplorerRemoveButton.IsEnabled = true;
        }
    }

    private async System.Threading.Tasks.Task ChangeExplorerRegistrationAsync(bool register)
    {
        if (IsPackaged) return;
        if (Updates.IsInstalling || _explorerBusy || _packsClosed) return;
        _explorerBusy = true;
        ExplorerAddButton.IsEnabled = ExplorerRemoveButton.IsEnabled = false;
        try
        {
            var state = await System.Threading.Tasks.Task.Run(() =>
                register ? _explorerMenu.Register(ExplorerExePath(), UiText.ExplorerMenuLabel)
                         : _explorerMenu.Remove(ExplorerExePath()));
            if (!_packsClosed) ExplorerMenuStatus.Text = ExplorerStateLabel(state);
        }
        catch (Exception)
        {
            if (!_packsClosed) ExplorerMenuStatus.Text = UiText.ExplorerUnavailable;
        }
        finally
        {
            _explorerBusy = false;
            if (!_packsClosed) ExplorerAddButton.IsEnabled = ExplorerRemoveButton.IsEnabled = true;
        }
    }

    private async void ExplorerAdd_Click(object sender, RoutedEventArgs e) =>
        await ChangeExplorerRegistrationAsync(register: true);

    private async void ExplorerRemove_Click(object sender, RoutedEventArgs e) =>
        await ChangeExplorerRegistrationAsync(register: false);

    /// <summary>Shows the effective registration; null only reads it. Windows' own Startup apps switch is never changed.</summary>
    private async System.Threading.Tasks.Task ChangeStartupAsync(bool? enable)
    {
        if (IsPackaged || _startupBusy || _packsClosed) return;
        _startupBusy = true;
        StartupToggle.IsEnabled = false;
        StartupState state;
        try
        {
            string executable = ExplorerExePath();
            state = await System.Threading.Tasks.Task.Run(() => enable switch
            {
                true => _startup.Enable(executable),
                false => _startup.Disable(executable),
                null => _startup.GetState(executable)
            });
        }
        catch (Exception) { state = StartupState.Unavailable; }
        finally { _startupBusy = false; }
        if (_packsClosed) return;
        _showingStartupState = true;
        StartupToggle.IsOn = state == StartupState.Enabled;
        _showingStartupState = false;
        StartupToggle.IsEnabled = state is StartupState.Enabled or StartupState.Disabled;
        string? message = state switch
        {
            StartupState.DisabledByWindows => UiText.StartupDisabledByWindows,
            StartupState.Conflict => UiText.StartupConflict,
            StartupState.Unavailable => UiText.StartupUnavailable,
            _ => null
        };
        StartupStatus.Text = message ?? "";
        StartupStatus.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void StartupToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_showingStartupState) return;
        await ChangeStartupAsync(StartupToggle.IsOn);
    }

    private async System.Threading.Tasks.Task RefreshFFmpegAsync()
    {
        if (_ffmpegInstall is not null || _ffmpegChecking || _packsClosed) return;
        _ffmpegChecking = true;
        FFmpegRefreshButton.IsEnabled = false;
        bool offerInstall = !IsPackaged;
        try
        {
            var (installed, winget) = await System.Threading.Tasks.Task.Run(() =>
                (_ffmpeg.FindInstalled() is not null, offerInstall && _ffmpeg.IsWingetAvailable()));
            if (_packsClosed) return;
            FFmpegStatus.Text = installed ? UiText.FFmpegFound : winget || !offerInstall ? UiText.FFmpegMissing : UiText.FFmpegWingetMissing;
            FFmpegInstallButton.IsEnabled = !installed && winget;
        }
        catch (Exception)
        {
            if (!_packsClosed) FFmpegStatus.Text = UiText.FFmpegMissing;
        }
        finally
        {
            _ffmpegChecking = false;
            if (!_packsClosed) FFmpegRefreshButton.IsEnabled = true;
        }
    }

    private async void FFmpegInstall_Click(object sender, RoutedEventArgs e)
    {
        if (IsPackaged || _ffmpegInstall is not null || _ffmpegChecking || _packsClosed) return;
        using var cancellation = new System.Threading.CancellationTokenSource();
        _ffmpegInstall = cancellation;
        FFmpegInstallButton.IsEnabled = FFmpegRefreshButton.IsEnabled = false;
        FFmpegCancelButton.IsEnabled = true;
        FFmpegProgress.Visibility = Visibility.Visible;
        FFmpegStatus.Text = UiText.FFmpegInstalling;
        FFmpegInstallOutcome outcome;
        try
        {
            outcome = await System.Threading.Tasks.Task.Run(() => _ffmpeg.InstallAsync(cancellation.Token));
        }
        catch (Exception)
        {
            outcome = FFmpegInstallOutcome.Failed;
        }
        finally
        {
            _ffmpegInstall = null;
        }
        if (_packsClosed) return;
        FFmpegProgress.Visibility = Visibility.Collapsed;
        FFmpegCancelButton.IsEnabled = false;
        FFmpegRefreshButton.IsEnabled = true;
        FFmpegInstallButton.IsEnabled = outcome != FFmpegInstallOutcome.Installed;
        FFmpegStatus.Text = outcome switch
        {
            FFmpegInstallOutcome.Installed => UiText.FFmpegInstalled,
            FFmpegInstallOutcome.WingetMissing => UiText.FFmpegWingetMissing,
            FFmpegInstallOutcome.Canceled => UiText.FFmpegCanceled,
            FFmpegInstallOutcome.NotFoundAfterInstall => UiText.FFmpegNotFoundAfterInstall,
            _ => UiText.FFmpegFailed
        };
    }

    private void FFmpegCancel_Click(object sender, RoutedEventArgs e) => _ffmpegInstall?.Cancel();

    private async void FFmpegRefresh_Click(object sender, RoutedEventArgs e) => await RefreshFFmpegAsync();

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        this.Close();
    }

    private void StartupUpdatesToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loadingUpdatePreference && App.Current is App { SettingsService: PowerTrove.Application.Configuration.IUpdateCheckSettings settings })
            settings.SetCheckForUpdatesOnStartup(StartupUpdatesToggle.IsOn);
    }

    private void AutomaticPackUpdatesToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loadingPackUpdatePreference && App.Current is App { SettingsService: IPackUpdateCheckSettings settings } app)
        {
            settings.SetCheckForPackUpdatesAutomatically(AutomaticPackUpdatesToggle.IsOn);
            app.PackUpdates.SetEnabled(AutomaticPackUpdatesToggle.IsOn);
        }
    }
    private async void InstallUpdate_Click(object sender, RoutedEventArgs e) => await Updates.InstallAsync();
    private void CancelUpdate_Click(object sender, RoutedEventArgs e) => Updates.CancelInstallation();

    private async void CheckForUpdates_Click(object sender, RoutedEventArgs e) => await Updates.CheckAsync();
    private async void ShowNotices_Click(object sender, RoutedEventArgs e)
    {
        NoticesButton.IsEnabled = false;
        NoticesStatus.Text = string.Empty;
        try
        {
            string notice = System.IO.Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.txt");
            if (!System.IO.File.Exists(notice)) { NoticesStatus.Text = UiText.SettingsNoticesUnavailable; return; }
            var result = await System.Threading.Tasks.Task.Run(() => new ShellNavigator().OpenFolderAndSelect(new[] { notice }))
                .WaitAsync(TimeSpan.FromSeconds(5));
            if (!result.IsSuccess) NoticesStatus.Text = UiText.SettingsNoticesUnavailable;
        }
        catch (Exception ex) when (ex is TimeoutException or System.IO.IOException or UnauthorizedAccessException)
        {
            NoticesStatus.Text = UiText.SettingsNoticesUnavailable;
        }
        finally { NoticesButton.IsEnabled = true; }
    }
    private async void OpenReleasePage_Click(object sender, RoutedEventArgs e) => await Updates.OpenReleasePageAsync();

    private void CancelPackInstall_Click(object sender, RoutedEventArgs e) { _packInstallCancellation?.Cancel(); Packs?.Cancel(); }
    private async void InstallPackFromFile_Click(object sender, RoutedEventArgs e)
    {
        if (Updates.IsInstalling || _packInstallCancellation is not null || Packs?.CanOperate != true) return;
        using var cancel = new System.Threading.CancellationTokenSource();
        _packInstallCancellation = cancel;
        InstallFromFileButton.IsEnabled = false; CancelPackInstallButton.IsEnabled = true;
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            picker.FileTypeFilter.Add(".cbpack");
            var file = await picker.PickSingleFileAsync();
            cancel.Token.ThrowIfCancellationRequested();
            if (file is null) return;
            await Packs.InstallFileAsync(file.Path, cancel.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!_packsClosed) PackInstallStatus.Text = UiText.PacksInstallFailed; }
        finally
        {
            _packInstallCancellation = null;
            if (!_packsClosed) { InstallFromFileButton.IsEnabled = Packs?.CanOperate == true; CancelPackInstallButton.IsEnabled = Packs?.IsBusy == true; }
        }
    }

    private async void Packs_Expanding(Microsoft.UI.Xaml.Controls.Expander sender, Microsoft.UI.Xaml.Controls.ExpanderExpandingEventArgs args)
    { if (!Updates.IsInstalling && Packs is not null) await Packs.OpenAsync(); }
    private async void RefreshPacks_Click(object sender, RoutedEventArgs e) { if (!Updates.IsInstalling && Packs is not null) await Packs.OpenAsync(); }
    private async void InstallPack_Click(object sender, RoutedEventArgs e)
    {
        if (Updates.IsInstalling || _packDialogOpen || Packs?.CanOperate != true || sender is not FrameworkElement { Tag: string id }) return;
        var row = System.Linq.Enumerable.SingleOrDefault(Packs.Rows, row => row.Id == id);
        if (row?.CanDownload != true) return;
        _packDialogOpen = true;
        try
        {
            var offer = await Packs.PrepareInstallAsync(id);
            if (offer is null || _packsClosed) return;
            var dialog = new ContentDialog { Title = offer.Name, Content = UiText.PackDownloadConsent(UiText.PackNameVersion(offer.Name, offer.Version), ActionPackRow.FormatSize(offer.DownloadBytes), offer.Publisher),
                PrimaryButtonText = row.PrimaryText, CloseButtonText = UiText.CommonCancel, DefaultButton = ContentDialogButton.Close, XamlRoot = Content.XamlRoot };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary && !_packsClosed) await Packs.InstallAsync(offer);
        }
        catch (Exception) { if (!_packsClosed) PackInstallStatus.Text = UiText.PacksInstallFailed; }
        finally { _packDialogOpen = false; }
    }
    private async void UpdateAllPacks_Click(object sender, RoutedEventArgs e)
    {
        if (Updates.IsInstalling || _packDialogOpen || Packs?.CanOperate != true || !Packs.HasUpdates) return;
        _packDialogOpen = true;
        try
        {
            var offers = await Packs.PrepareUpdatesAsync();
            if (offers.Count == 0 || _packsClosed) return;
            string size = ActionPackRow.FormatSize(System.Linq.Enumerable.Sum(offers, offer => offer.DownloadBytes));
            string publishers = string.Join(", ", System.Linq.Enumerable.Distinct(System.Linq.Enumerable.Select(offers, offer => offer.Publisher)));
            string subject = offers.Count == 1 ? UiText.PackNameVersion(offers[0].Name, offers[0].Version)
                : offers.Count.ToString(System.Globalization.CultureInfo.CurrentCulture);
            var dialog = new ContentDialog { Title = UiText.PacksUpdateAllTitle, Content = UiText.PackUpdateAllConsent(offers.Count, subject, size, publishers),
                PrimaryButtonText = UiText.PacksUpdateAllTitle, CloseButtonText = UiText.CommonCancel, DefaultButton = ContentDialogButton.Close, XamlRoot = Content.XamlRoot };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary && !_packsClosed) await Packs.InstallAllAsync(offers);
        }
        catch (Exception) { if (!_packsClosed) PackInstallStatus.Text = UiText.PacksInstallFailed; }
        finally { _packDialogOpen = false; }
    }

    // Only official pack rows carry a fixed https://www.powertrove.net address; nothing else is opened.
    private async void PackWebsite_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string url } || !url.StartsWith("https://www.powertrove.net/", StringComparison.Ordinal)) return;
        try { await Launcher.LaunchUriAsync(new Uri(url)); }
        catch (Exception) { }
    }

    private async void UninstallPack_Click(object sender, RoutedEventArgs e)
    {
        if (Updates.IsInstalling || _packDialogOpen || Packs?.CanOperate != true || sender is not FrameworkElement { Tag: string id }) return;
        var row = System.Linq.Enumerable.SingleOrDefault(Packs.Rows, row => row.Id == id);
        if (row?.CanUninstall != true) return;
        _packDialogOpen = true;
        try
        {
            var dialog = new ContentDialog { Title = row.Name, Content = row.IsBuiltIn ? UiText.PackRemoveConsent(row.Name) : UiText.PackRemoveConsentNoBundled(row.Name), PrimaryButtonText = UiText.PacksUninstall,
                CloseButtonText = UiText.CommonCancel, DefaultButton = ContentDialogButton.Close, XamlRoot = Content.XamlRoot };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary && !_packsClosed) await Packs.UninstallAsync(id);
        }
        catch (Exception) { if (!_packsClosed) PackInstallStatus.Text = UiText.PacksInstallFailed; }
        finally { _packDialogOpen = false; }
    }
}
