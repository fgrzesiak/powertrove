using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using PowerTrove.Application.Documents;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;
using Launcher = Windows.System.Launcher;
using VirtualKey = Windows.System.VirtualKey;
using VirtualKeyModifiers = Windows.System.VirtualKeyModifiers;

namespace PowerTrove.Desktop;

/// <summary>
/// The host's document viewer and editor for one text file (Markdown first). The pack renders: every preview is a
/// <see cref="DocumentPreviewRequest"/> to the bound action, with the unsaved text as a draft. The host shows the
/// sanitized HTML in WebView2 without scripts, network access, downloads or navigation, and serves only images from
/// the document's folder. The file changes only on Save; unsaved text is kept for recovery until saved or discarded.
/// </summary>
public sealed class DocumentEditorView : HostedView
{
    private enum Mode { Preview, Split, Edit }

    private static Task<CoreWebView2Environment>? s_environment;
    private readonly Func<IAction?> _action;
    private readonly ContextSnapshot _origin;
    private readonly DocumentRecoveryStore _recovery;
    private readonly string _webViewData;
    private readonly CancellationTokenSource _closing = new();
    private readonly TextBox _editor = new()
    {
        AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, IsSpellCheckEnabled = false, IsTextPredictionEnabled = false,
        FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 14, MaxLength = DocumentPreviewRequest.MaximumDraftLength,
        BorderThickness = new Thickness(0), Padding = new Thickness(16, 12, 16, 12)
    };
    private readonly WebView2 _web = new();
    private readonly Grid _content = new();
    private readonly TextBlock _previewMessage = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(24), Visibility = Visibility.Collapsed };
    private readonly SelectorBar _modes = new();
    private readonly Button _save = new() { Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["AccentButtonStyle"], IsEnabled = false };
    private readonly Button _saveAs = new() { IsEnabled = false };
    private readonly TextBlock _fileName = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _details = new() { VerticalAlignment = VerticalAlignment.Center, Opacity = .75 };
    private readonly InfoBar _notice = new() { IsClosable = true, IsOpen = false };
    private readonly DispatcherQueueTimer _renderTimer;
    private readonly DispatcherQueueTimer _recoveryTimer;
    private TextDocumentSession? _session;
    private Grid? _editorPanel;
    private string _previewWarnings = string.Empty;
    private bool _documentShown;
    private CancellationTokenSource? _rendering;
    private string _html = string.Empty;
    private long _revision;
    private double _scroll;
    private bool _webReady, _loading = true, _dirty, _editable, _dialogOpen, _closed;
    private Mode _mode;

    private static string T(string key) => UiText.DocumentEditor(key);

    /// <param name="action">Resolves the bound viewer action again for each preview; null once its pack is gone.</param>
    public DocumentEditorView(Func<IAction?> action, ContextSnapshot snapshot, bool edit, string userRoot)
    {
        _action = action; _origin = snapshot; _mode = edit ? Mode.Split : Mode.Preview;
        _recovery = new DocumentRecoveryStore(Path.Combine(userRoot, "recovery"));
        _webViewData = Path.Combine(userRoot, "webview2");
        string name = Path.GetFileName(CurrentPath);
        Title = UiText.Format(T("WindowTitle"), name);
        PreferredSize = new(1240, 840);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var toolbar = new Grid { Padding = new Thickness(16, 4, 16, 8), ColumnSpacing = 12 };
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        foreach (var mode in new[] { Mode.Preview, Mode.Split, Mode.Edit })
            _modes.Items.Add(new SelectorBarItem { Text = T(mode.ToString()), Tag = mode, IsEnabled = mode == Mode.Preview });
        _modes.SelectedItem = _modes.Items[0];
        _modes.SelectionChanged += (_, _) => { if (_modes.SelectedItem?.Tag is Mode mode) SetMode(mode); };
        AutomationProperties.SetName(_modes, T("Modes"));
        toolbar.Children.Add(_modes);
        _fileName.Text = name; Grid.SetColumn(_fileName, 1); toolbar.Children.Add(_fileName);
        _saveAs.Content = T("SaveAs"); _saveAs.Click += async (_, _) => await SaveAsync(saveAs: true);
        _saveAs.KeyboardAccelerators.Add(new KeyboardAccelerator { Key = Windows.System.VirtualKey.S, Modifiers = Windows.System.VirtualKeyModifiers.Control | Windows.System.VirtualKeyModifiers.Shift });
        Grid.SetColumn(_saveAs, 2); toolbar.Children.Add(_saveAs);
        _save.Content = T("Save"); _save.Click += async (_, _) => await SaveAsync(saveAs: false);
        _save.KeyboardAccelerators.Add(new KeyboardAccelerator { Key = Windows.System.VirtualKey.S, Modifiers = Windows.System.VirtualKeyModifiers.Control });
        Grid.SetColumn(_save, 3); toolbar.Children.Add(_save);
        root.Children.Add(toolbar);

        _notice.Margin = new Thickness(16, 0, 16, 8);
        Grid.SetRow(_notice, 1); root.Children.Add(_notice);

        _content.ColumnDefinitions.Add(new ColumnDefinition());
        _content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
        _content.ColumnDefinitions.Add(new ColumnDefinition());
        _content.BorderThickness = new Thickness(0, 1, 0, 1);
        _content.BorderBrush = Brush("CardStrokeColorDefaultBrush");
        AutomationProperties.SetName(_editor, T("EditorName"));
        var editorPanel = new Grid();
        editorPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        editorPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        editorPanel.Children.Add(CreateFormatBar());
        Grid.SetRow(_editor, 1); editorPanel.Children.Add(_editor);
        _editorPanel = editorPanel;
        _content.Children.Add(editorPanel);
        var divider = new Border { Background = Brush("CardStrokeColorDefaultBrush") };
        Grid.SetColumn(divider, 1); _content.Children.Add(divider);
        var preview = new Grid { Background = Brush("SolidBackgroundFillColorBaseBrush") };
        AutomationProperties.SetName(_web, T("PreviewName"));
        preview.Children.Add(_web); preview.Children.Add(_previewMessage);
        Grid.SetColumn(preview, 2); _content.Children.Add(preview);
        Grid.SetRow(_content, 2); root.Children.Add(_content);

        var footer = new Grid { Padding = new Thickness(16, 6, 16, 8), ColumnSpacing = 16 };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.Children.Add(_status);
        Grid.SetColumn(_details, 1); footer.Children.Add(_details);
        Grid.SetRow(footer, 3); root.Children.Add(footer);
        Content = root;

        _status.Text = T("Loading");
        _editor.IsReadOnly = true;
        _editor.TextChanged += (_, _) => TextEdited();
        _renderTimer = DispatcherQueue.CreateTimer(); _renderTimer.Interval = TimeSpan.FromMilliseconds(200); _renderTimer.IsRepeating = false;
        _renderTimer.Tick += async (_, _) => await RenderAsync();
        _recoveryTimer = DispatcherQueue.CreateTimer(); _recoveryTimer.Interval = TimeSpan.FromSeconds(2); _recoveryTimer.IsRepeating = false;
        _recoveryTimer.Tick += async (_, _) => await KeepDraftAsync();
        SetMode(_mode, select: false);
        // WebView2 starts its browser process in parallel with reading the file and rendering the first preview.
        _ = InitializeWebAsync();
        Loaded += async (_, _) => await LoadAsync(edit);
        Closed += (_, _) => Cleanup();
    }

    private string CurrentPath => _session?.Path ?? ((FileSelection)_origin.Content).Files[0].Path!;

    protected internal override Control? InitialFocus => _mode == Mode.Preview ? null : _editor;
    public override bool CanCloseForUpdate => !_dirty;
    protected override bool NeedsCloseConfirmation => _dirty;

    protected override async void ConfirmClose()
    {
        if (_dialogOpen) return;
        var dialog = new ContentDialog
        {
            Title = UiText.Format(T("UnsavedTitle"), Path.GetFileName(CurrentPath)), Content = T("UnsavedText"),
            PrimaryButtonText = T("Save"), SecondaryButtonText = T("DontSave"), CloseButtonText = UiText.CommonCancel,
            DefaultButton = ContentDialogButton.Primary, XamlRoot = XamlRoot
        };
        var choice = await ShowDialogAsync(dialog);
        if (choice == ContentDialogResult.Primary) { if (await SaveAsync(saveAs: false)) CloseConfirmed(); }
        else if (choice == ContentDialogResult.Secondary) { _recovery.Delete(CurrentPath); _dirty = false; CloseConfirmed(); }
    }

    private async Task LoadAsync(bool edit)
    {
        var firstPreview = RenderAsync();
        var (session, failure) = await TextDocumentSession.OpenAsync(CurrentPath, _closing.Token);
        if (_closed) return;
        if (session is null)
        {
            _status.Text = T(failure switch { TextDocumentLoadFailure.TooLarge => "TooLarge", TextDocumentLoadFailure.NotText => "NotText", _ => "OpenFailed" });
            _loading = false;
            await firstPreview;
            return;
        }
        _session = session;
        _editable = session.SavedText.Length <= DocumentPreviewRequest.MaximumDraftLength;
        _editor.Text = session.SavedText;
        _editor.IsReadOnly = !_editable;
        foreach (var item in _modes.Items) item.IsEnabled = _editable || item.Tag is Mode.Preview;
        _saveAs.IsEnabled = true;
        _loading = false;
        _details.Text = Details(session);
        if (!_editable) { ShowNotice(InfoBarSeverity.Informational, T("ViewOnly")); SetMode(Mode.Preview); }
        else if (edit) SetMode(Mode.Split);
        _recovery.Prune();
        if (_editable && await _recovery.LoadAsync(session.Path, _closing.Token) is { } draft && draft.Text != session.SavedText && !_closed)
            OfferRecovery(draft);
        await firstPreview;
        if (_closed) return;
        _status.Text = _previewWarnings.Length == 0 ? Counts(session.SavedText) : Counts(session.SavedText) + " · " + _previewWarnings;
    }

    private void OfferRecovery(RecoveredDraft draft)
    {
        var restore = new Button { Content = T("Restore") };
        var discard = new Button { Content = T("Discard") };
        restore.Click += (_, _) => { _notice.IsOpen = false; _editor.Text = draft.Text; SetMode(_mode == Mode.Preview ? Mode.Split : _mode); };
        discard.Click += (_, _) => { _notice.IsOpen = false; _recovery.Delete(CurrentPath); };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(restore); actions.Children.Add(discard);
        ShowNotice(InfoBarSeverity.Warning, UiText.Format(T("RecoveryText"), draft.SavedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)), actions);
    }

    private void ShowNotice(InfoBarSeverity severity, string message, UIElement? actions = null)
    {
        _notice.Severity = severity; _notice.Message = message; _notice.ActionButton = null;
        _notice.Content = actions; _notice.IsOpen = true;
    }

    /// <summary>Word-like formatting buttons; each inserts Markdown syntax as one undoable change.</summary>
    private StackPanel CreateFormatBar()
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Padding = new Thickness(8, 4, 8, 4) };
        AutomationProperties.SetName(bar, T("FormatBar"));
        var headings = new MenuFlyout();
        foreach (var (style, key, number) in new[] { (MarkdownLineStyle.Paragraph, "FormatParagraph", VirtualKey.Number0), (MarkdownLineStyle.Heading1, "FormatHeading1", VirtualKey.Number1),
            (MarkdownLineStyle.Heading2, "FormatHeading2", VirtualKey.Number2), (MarkdownLineStyle.Heading3, "FormatHeading3", VirtualKey.Number3) })
        {
            var item = new MenuFlyoutItem { Text = T(key) };
            item.Click += (_, _) => Format((text, start, length) => MarkdownFormatting.SetLineStyle(text, start, length, style));
            item.KeyboardAccelerators.Add(new KeyboardAccelerator { Key = number, Modifiers = VirtualKeyModifiers.Control });
            headings.Items.Add(item);
        }
        var heading = new DropDownButton { Content = T("FormatHeading"), Flyout = headings, Padding = new Thickness(10, 4, 10, 4) };
        ToolTipService.SetToolTip(heading, T("FormatHeadingTip"));
        bar.Children.Add(heading);
        bar.Children.Add(Separator());
        bar.Children.Add(Tool(Text("B", bold: true), "FormatBold", (t, s, l) => MarkdownFormatting.ToggleWrap(t, s, l, "**", T("FormatPlaceholder")), VirtualKey.B));
        bar.Children.Add(Tool(Text("I", italic: true), "FormatItalic", (t, s, l) => MarkdownFormatting.ToggleWrap(t, s, l, "*", T("FormatPlaceholder")), VirtualKey.I));
        bar.Children.Add(Tool(Text("S", strike: true), "FormatStrike", (t, s, l) => MarkdownFormatting.ToggleWrap(t, s, l, "~~", T("FormatPlaceholder"))));
        bar.Children.Add(Tool(Glyph(""), "FormatCode", (t, s, l) => MarkdownFormatting.ToggleCode(t, s, l, "\r", T("FormatCodePlaceholder"))));
        bar.Children.Add(Separator());
        bar.Children.Add(Tool(Glyph(""), "FormatBullets", (t, s, l) => MarkdownFormatting.SetLineStyle(t, s, l, MarkdownLineStyle.Bullet)));
        bar.Children.Add(Tool(Text("1."), "FormatNumbers", (t, s, l) => MarkdownFormatting.SetLineStyle(t, s, l, MarkdownLineStyle.Numbered)));
        bar.Children.Add(Tool(Text("☐"), "FormatTasks", (t, s, l) => MarkdownFormatting.SetLineStyle(t, s, l, MarkdownLineStyle.Task)));
        bar.Children.Add(Tool(Text("”"), "FormatQuote", (t, s, l) => MarkdownFormatting.SetLineStyle(t, s, l, MarkdownLineStyle.Quote)));
        bar.Children.Add(Separator());
        bar.Children.Add(Tool(Glyph(""), "FormatLink", (t, s, l) => MarkdownFormatting.InsertLink(t, s, l, T("FormatLinkText")), VirtualKey.K));
        var image = Tool(Glyph(""), "FormatImage", null);
        image.Click += async (_, _) => await InsertImageAsync();
        bar.Children.Add(image);
        bar.Children.Add(Tool(Glyph(""), "FormatTable", (t, s, l) => MarkdownFormatting.InsertTable(t, s, l, "\r", T("FormatTableColumn"), T("FormatTableCell"))));
        bar.Children.Add(Tool(Text("―"), "FormatRule", (t, s, l) => MarkdownFormatting.InsertBlock(t, s, l, "---", "\r", 3, 0)));
        return bar;
    }

    private Button Tool(UIElement content, string key, Func<string, int, int, TextEdit>? command, VirtualKey? shortcut = null)
    {
        var button = new Button { Content = content, MinWidth = 34, Padding = new Thickness(6, 4, 6, 4), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0) };
        ToolTipService.SetToolTip(button, shortcut is { } letter ? UiText.Format(T("FormatShortcut"), T(key), "Ctrl+" + letter) : T(key));
        AutomationProperties.SetName(button, T(key));
        if (command is not null) button.Click += (_, _) => Format(command);
        if (shortcut is { } accelerator)
            button.KeyboardAccelerators.Add(new KeyboardAccelerator { Key = accelerator, Modifiers = VirtualKeyModifiers.Control });
        button.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        return button;
    }

    private static FontIcon Glyph(string glyph) => new() { Glyph = glyph, FontSize = 14 };

    private static TextBlock Text(string text, bool bold = false, bool italic = false, bool strike = false) => new()
    {
        Text = text, FontSize = 14, FontWeight = bold ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal,
        FontStyle = italic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal,
        TextDecorations = strike ? Windows.UI.Text.TextDecorations.Strikethrough : Windows.UI.Text.TextDecorations.None
    };

    private static Border Separator() => new() { Width = 1, Margin = new Thickness(4, 6, 4, 6), Background = Brush("CardStrokeColorDefaultBrush") };

    /// <summary>Applies one toolbar command as a single undoable replacement and keeps typing where it makes sense.</summary>
    private void Format(Func<string, int, int, TextEdit> command)
    {
        if (!_editable || _editor.IsReadOnly || _session is null || _mode == Mode.Preview) return;
        var edit = command(_editor.Text, _editor.SelectionStart, _editor.SelectionLength);
        if (edit.Replacement.Length + _editor.Text.Length - edit.Length > _editor.MaxLength) return;
        _editor.Select(edit.Start, edit.Length);
        _editor.SelectedText = edit.Replacement;
        _editor.Select(edit.SelectionStart, edit.SelectionLength);
        _editor.Focus(FocusState.Programmatic);
    }

    /// <summary>
    /// The viewer, HTML and PDF read images only from the document's folder tree, so a picked image from elsewhere is
    /// copied into its images folder and referenced relatively; the document then stays portable when shared.
    /// </summary>
    private async Task InsertImageAsync()
    {
        if (!_editable || _session is null || _mode == Mode.Preview) return;
        var picker = new Windows.Storage.Pickers.FileOpenPicker { ViewMode = Windows.Storage.Pickers.PickerViewMode.Thumbnail };
        foreach (string extension in new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg", ".bmp", ".avif" }) picker.FileTypeFilter.Add(extension);
        if (App.Current is App { Shell: { } shell }) WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(shell));
        string? path;
        try { path = (await picker.PickSingleFileAsync())?.Path; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return; }
        if (path is null || _closed) return;
        // Images from elsewhere are copied next to the document; the selection is restored after the picker.
        int selectionStart = _editor.SelectionStart, selectionLength = _editor.SelectionLength;
        var import = await DocumentImages.ImportAsync(_session.Path, path, _closing.Token);
        if (_closed) return;
        if (import.RelativePath is not { } relative)
        {
            ShowNotice(InfoBarSeverity.Warning, T("FormatImage" + import.Failure));
            return;
        }
        _editor.Select(selectionStart, selectionLength);
        Format((text, start, length) => MarkdownFormatting.InsertImage(text, start, length, T("FormatImageText"), relative));
        if (import.Copied) ShowNotice(InfoBarSeverity.Success, UiText.Format(T("FormatImageCopied"), relative));
    }

    private void SetMode(Mode mode, bool select = true)
    {
        if (!_editable && mode != Mode.Preview && !_loading) mode = Mode.Preview;
        _mode = mode;
        _content.ColumnDefinitions[0].Width = mode == Mode.Preview ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        _content.ColumnDefinitions[1].Width = mode == Mode.Split ? new GridLength(1) : new GridLength(0);
        _content.ColumnDefinitions[2].Width = mode == Mode.Edit ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        if (_editorPanel is not null) _editorPanel.Visibility = mode == Mode.Preview ? Visibility.Collapsed : Visibility.Visible;
        if (select && _modes.Items.FirstOrDefault(i => i.Tag is Mode m && m == mode) is { } item && !ReferenceEquals(_modes.SelectedItem, item))
            _modes.SelectedItem = item;
        if (mode != Mode.Preview && !_loading) _editor.Focus(FocusState.Programmatic);
    }

    private void TextEdited()
    {
        if (_loading || _session is null || _closed) return;
        bool dirty = TextDocumentSession.NormalizeNewLines(_editor.Text) != _session.SavedText;
        if (dirty != _dirty) { _dirty = dirty; UpdateDirty(); }
        _save.IsEnabled = _dirty;
        _renderTimer.Stop(); _renderTimer.Start();
        _recoveryTimer.Stop(); if (_dirty) _recoveryTimer.Start();
    }

    private void UpdateDirty()
    {
        string name = Path.GetFileName(CurrentPath);
        _fileName.Text = _dirty ? UiText.Format(T("Unsaved"), name) : name;
        _save.IsEnabled = _dirty;
    }

    private async Task KeepDraftAsync()
    {
        if (!_dirty || _closed || _session is null) return;
        try { await _recovery.SaveAsync(_session.Path, TextDocumentSession.NormalizeNewLines(_editor.Text), _closing.Token); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException) { /* Recovery is best effort; the editor keeps the text. */ }
    }

    private ContextSnapshot FreshSnapshot()
    {
        string path = CurrentPath;
        long? size = File.Exists(path) ? new FileInfo(path).Length : null;
        var now = DateTimeOffset.UtcNow;
        var file = new SelectedFile(path, Path.GetFileName(path), Path.GetExtension(path).ToLowerInvariant(), null, size, true, true, null);
        // The editor re-issues the user's own document for each preview; a long session must not expire with the palette's snapshot.
        return _origin with { InvocationId = Guid.NewGuid(), Content = new FileSelection([file]), CapturedAt = now, ExpiresAt = now.AddMinutes(2) };
    }

    private async Task RenderAsync()
    {
        if (_closed) return;
        _rendering?.Cancel(); _rendering?.Dispose();
        var cts = _rendering = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        if (_action() is not { } action) { ShowPreviewMessage(T("PackUnavailable")); return; }
        string? draft = _dirty ? TextDocumentSession.NormalizeNewLines(_editor.Text) : null;
        if (draft is not null && draft.Length > DocumentPreviewRequest.MaximumDraftLength) { _status.Text = T("PreviewTooLarge"); return; }
        if (draft is not null) _status.Text = T("Rendering");
        ActionPreview preview;
        try { preview = await action.PreviewAsync(FreshSnapshot(), DocumentPreviewRequest.Create(null, draft), cts.Token); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { if (!cts.IsCancellationRequested) ShowPreviewMessage(T("PreviewFailed")); return; }
        if (cts.IsCancellationRequested || _closed) return;
        if (preview.IsBlocked || preview.DocumentPreview is not { IsValid: true } document)
        {
            // A pack released before the document viewer answers with a valid preview but no document.
            ShowPreviewMessage(preview.IsBlocked ? preview.PreviewText ?? T("PreviewFailed") : T("PackTooOld"));
            return;
        }
        _html = document.Html;
        string counts = Counts(draft ?? _session?.SavedText ?? string.Empty);
        _previewWarnings = string.Join(" ", preview.Warnings);
        _status.Text = preview.Warnings.Count == 0 ? counts : counts + " · " + _previewWarnings;
        ToolTipService.SetToolTip(_status, preview.Warnings.Count == 0 ? null : string.Join("\n", preview.Warnings));
        await ShowHtmlAsync();
    }

    private void ShowPreviewMessage(string message)
    {
        _previewMessage.Text = message; _previewMessage.Visibility = Visibility.Visible; _web.Visibility = Visibility.Collapsed;
        _status.Text = message;
    }

    private async Task InitializeWebAsync()
    {
        int step = 0;
        try
        {
            // Only a missing runtime is reported as such; every other failure shows its step and code.
            // A load failure of WebView2's own component (for example the wrong architecture) is not a missing runtime.
            string? runtime;
            try { runtime = CoreWebView2Environment.GetAvailableBrowserVersionString(); }
            catch (Exception ex) when (ex is not OutOfMemoryException && ex.HResult == unchecked((int)0x80070002)) { runtime = null; }
            if (string.IsNullOrEmpty(runtime)) { if (!_closed) ShowPreviewMessage(T("WebViewMissing")); return; }
            step = 1;
            Directory.CreateDirectory(_webViewData);
            step = 2;
            var creation = s_environment ??= CoreWebView2Environment.CreateWithOptionsAsync(null, _webViewData, new CoreWebView2EnvironmentOptions()).AsTask();
            CoreWebView2Environment environment;
            // A failed creation must not poison every later editor in this process.
            try { environment = await creation; }
            catch { if (ReferenceEquals(s_environment, creation)) s_environment = null; throw; }
            step = 3;
            if (!_web.IsLoaded)
            {
                var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _web.Loaded += (_, _) => loaded.TrySetResult();
                if (!_web.IsLoaded) await loaded.Task;
            }
            await _web.EnsureCoreWebView2Async(environment);
            if (_closed) return;
            step = 4;
            var core = _web.CoreWebView2;
            var settings = core.Settings;
            settings.IsScriptEnabled = false; settings.IsWebMessageEnabled = false; settings.AreDevToolsEnabled = false;
            settings.AreDefaultScriptDialogsEnabled = false; settings.AreHostObjectsAllowed = false; settings.IsStatusBarEnabled = false;
            settings.IsPasswordAutosaveEnabled = false; settings.IsGeneralAutofillEnabled = false; settings.IsBuiltInErrorPageEnabled = false;
            settings.AreBrowserAcceleratorKeysEnabled = false;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, args) => Serve(environment, args);
            core.NavigationStarting += (_, args) => Navigating(args);
            core.NewWindowRequested += (sender, args) => { args.Handled = true; _ = OpenLinkAsync(args.Uri, args.IsUserInitiated); };
            core.DownloadStarting += (_, args) => { args.Cancel = true; args.Handled = true; };
            core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
            core.NavigationCompleted += async (_, args) => { _documentShown = args.IsSuccess; await RestoreScrollAsync(); };
            _webReady = true;
            await ShowHtmlAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!_closed) ShowPreviewMessage(UiText.Format(T("WebViewFailed"), step, ex.HResult.ToString("X8", CultureInfo.InvariantCulture)));
        }
    }

    private async Task ShowHtmlAsync()
    {
        if (!_webReady || _html.Length == 0 || _closed) return;
        _previewMessage.Visibility = Visibility.Collapsed; _web.Visibility = Visibility.Visible;
        if (_documentShown)
        {
            // Host-owned script swaps the rendered body in place: no reload, no flicker, same scroll position.
            // DOMParser documents are inert, and the page's own scripts stay disabled.
            try
            {
                string script = "(()=>{const d=new DOMParser().parseFromString(" + System.Text.Json.JsonSerializer.Serialize(_html)
                    + ",'text/html');document.title=d.title;document.body.replaceWith(document.adoptNode(d.body));return 1;})()";
                if (await _web.CoreWebView2.ExecuteScriptAsync(script) == "1") return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { }
        }
        // Host-owned script (page scripts stay disabled) keeps the reading position across re-renders.
        try { _scroll = double.TryParse(await _web.CoreWebView2.ExecuteScriptAsync("window.scrollY"), NumberStyles.Float, CultureInfo.InvariantCulture, out var y) ? y : 0; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _scroll = 0; }
        _web.CoreWebView2.Navigate(DocumentPreviewData.ResourceBaseUri + "?r=" + (++_revision).ToString(CultureInfo.InvariantCulture));
    }

    private async Task RestoreScrollAsync()
    {
        if (_scroll <= 0 || _closed) return;
        try { await _web.CoreWebView2.ExecuteScriptAsync("window.scrollTo(0," + _scroll.ToString("0", CultureInfo.InvariantCulture) + ")"); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }

    private void Navigating(CoreWebView2NavigationStartingEventArgs args)
    {
        if (args.Uri == DocumentPreviewData.ResourceBaseUri + "?r=" + _revision.ToString(CultureInfo.InvariantCulture)) return;
        args.Cancel = true;
        _ = OpenLinkAsync(args.Uri, args.IsUserInitiated);
    }

    /// <summary>Links never navigate the preview. Web and mail links open outside PowerTrove after the user confirms.</summary>
    private async Task OpenLinkAsync(string target, bool userInitiated)
    {
        if (!userInitiated || _closed) return;
        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri)) return;
        if (target.StartsWith(DocumentPreviewData.ResourceBaseUri, StringComparison.Ordinal)) { _status.Text = T("LocalLink"); return; }
        if (uri.Scheme is not ("http" or "https" or "mailto")) return;
        var dialog = new ContentDialog
        {
            Title = T("LinkTitle"), Content = new TextBlock { Text = uri.AbsoluteUri, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
            PrimaryButtonText = T("LinkOpen"), CloseButtonText = UiText.CommonCancel, DefaultButton = ContentDialogButton.Close, XamlRoot = XamlRoot
        };
        if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary) await Windows.System.Launcher.LaunchUriAsync(uri);
    }

    private void Serve(CoreWebView2Environment environment, CoreWebView2WebResourceRequestedEventArgs args)
    {
        string uri = args.Request.Uri;
        if (DocumentPreviewResources.IsDocument(uri))
        {
            args.Response = Response(environment, System.Text.Encoding.UTF8.GetBytes(_html), 200, "OK",
                "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\nContent-Security-Policy: default-src 'none'; img-src "
                + DocumentPreviewData.ResourceBaseUri.TrimEnd('/') + " data:; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'");
            return;
        }
        if (!uri.StartsWith(DocumentPreviewData.ResourceBaseUri, StringComparison.Ordinal))
        {
            // Nothing leaves this PC: every other address is answered locally with an empty refusal.
            args.Response = Response(environment, [], 403, "Blocked", "Cache-Control: no-store");
            return;
        }
        var deferral = args.GetDeferral();
        string document = CurrentPath;
        _ = Task.Run(async () =>
        {
            var resource = await DocumentPreviewResources.TryReadAsync(document, uri);
            DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    args.Response = resource is null
                        ? Response(environment, [], 404, "Not Found", "Cache-Control: no-store")
                        : Response(environment, resource.Content, 200, "OK", "Content-Type: " + resource.ContentType
                            + "\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nContent-Security-Policy: default-src 'none'; style-src 'unsafe-inline'; sandbox");
                }
                finally { deferral.Complete(); }
            });
        });
    }

    private static CoreWebView2WebResourceResponse Response(CoreWebView2Environment environment, byte[] content, int status, string reason, string headers) =>
        environment.CreateWebResourceResponse(new MemoryStream(content, writable: false).AsRandomAccessStream(), status, reason, headers);

    private async Task<bool> SaveAsync(bool saveAs)
    {
        if (_session is null || _closed || _dialogOpen) return false;
        string text = TextDocumentSession.NormalizeNewLines(_editor.Text);
        TextDocumentSaveResult result;
        string previous = _session.Path;
        if (saveAs)
        {
            string? target = await PickSaveTargetAsync();
            if (target is null) return false;
            result = await _session.SaveAsAsync(target, text, _closing.Token);
        }
        else
        {
            result = await _session.SaveAsync(text, cancellationToken: _closing.Token);
            if (result == TextDocumentSaveResult.ChangedOnDisk)
            {
                var dialog = new ContentDialog
                {
                    Title = T("ChangedTitle"), Content = T("ChangedText"), PrimaryButtonText = T("Overwrite"), SecondaryButtonText = T("SaveAs"),
                    CloseButtonText = UiText.CommonCancel, DefaultButton = ContentDialogButton.Close, XamlRoot = XamlRoot
                };
                var choice = await ShowDialogAsync(dialog);
                if (choice == ContentDialogResult.Secondary) return await SaveAsync(saveAs: true);
                if (choice != ContentDialogResult.Primary) return false;
                result = await _session.SaveAsync(text, overwriteExternalChanges: true, _closing.Token);
            }
        }
        if (result is TextDocumentSaveResult.ReadOnly or TextDocumentSaveResult.Failed)
        {
            ShowNotice(InfoBarSeverity.Error, T(result == TextDocumentSaveResult.ReadOnly ? "ReadOnly" : "SaveFailed"));
            return false;
        }
        _recovery.Delete(previous);
        if (!string.Equals(previous, _session.Path, StringComparison.OrdinalIgnoreCase)) _recovery.Delete(_session.Path);
        _dirty = TextDocumentSession.NormalizeNewLines(_editor.Text) != _session.SavedText;
        UpdateDirty();
        _details.Text = Details(_session);
        _notice.IsOpen = result == TextDocumentSaveResult.SavedAsUtf8;
        if (result == TextDocumentSaveResult.SavedAsUtf8) ShowNotice(InfoBarSeverity.Informational, T("SavedAsUtf8"));
        _status.Text = T("Saved");
        if (!string.Equals(previous, _session.Path, StringComparison.OrdinalIgnoreCase)) await RenderAsync();
        return true;
    }

    private async Task<string?> PickSaveTargetAsync()
    {
        var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = Path.GetFileNameWithoutExtension(CurrentPath) };
        string extension = Path.GetExtension(CurrentPath) is { Length: > 0 } e ? e : ".md";
        picker.FileTypeChoices.Add(T("FileType"), new List<string> { extension });
        if (App.Current is App { Shell: { } shell })
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(shell));
        try { return (await picker.PickSaveFileAsync())?.Path; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { ShowNotice(InfoBarSeverity.Error, T("SaveFailed")); return null; }
    }

    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        if (_dialogOpen) return ContentDialogResult.None;
        _dialogOpen = true;
        try { return await dialog.ShowAsync(); }
        finally { _dialogOpen = false; }
    }

    private static string Counts(string text)
    {
        int words = 0; bool inWord = false;
        foreach (char c in text)
        {
            bool letter = !char.IsWhiteSpace(c);
            if (letter && !inWord) words++;
            inWord = letter;
        }
        return UiText.Format(UiText.PluralString("DocumentEditor_Words", words), words) + " · " +
            UiText.Format(UiText.PluralString("DocumentEditor_Characters", text.Length), text.Length);
    }

    private static string Details(TextDocumentSession session) =>
        UiText.Format(T("Details"), session.EncodingName.ToUpperInvariant(), T(session.NewLine == "\n" ? "LineEndingsUnix" : "LineEndingsWindows"));

    private static Brush Brush(string key) =>
        Microsoft.UI.Xaml.Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush ? brush : new SolidColorBrush(Microsoft.UI.Colors.Transparent);

    private void Cleanup()
    {
        if (_closed) return;
        _closed = true;
        _renderTimer.Stop(); _recoveryTimer.Stop();
        // Unsaved text stays in the recovery store when the view closes without a choice (app exit).
        if (_dirty && _session is not null)
            try { _recovery.SaveAsync(_session.Path, TextDocumentSession.NormalizeNewLines(_editor.Text)).GetAwaiter().GetResult(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        _closing.Cancel(); _rendering?.Cancel();
        try { _web.Close(); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }
}
