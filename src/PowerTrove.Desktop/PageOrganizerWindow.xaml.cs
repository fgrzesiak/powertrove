using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PowerTrove.Abstractions;
using PowerTrove.Abstractions.Files;
using PowerTrove.Application.Commands;
using PowerTrove.Platform.Windows.Pdf;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace PowerTrove.Desktop;

/// <summary>One page card in the organizer: which page of which source PDF, plus extra rotation.</summary>
public sealed class PageTile : INotifyPropertyChanged
{
    private static readonly Windows.UI.Color[] SourceColors =
    {
        Windows.UI.Color.FromArgb(255, 0x25, 0x63, 0xEB), Windows.UI.Color.FromArgb(255, 0xD9, 0x46, 0xEF),
        Windows.UI.Color.FromArgb(255, 0x05, 0x96, 0x69), Windows.UI.Color.FromArgb(255, 0xEA, 0x58, 0x0C),
        Windows.UI.Color.FromArgb(255, 0x7C, 0x3A, 0xED), Windows.UI.Color.FromArgb(255, 0xDC, 0x26, 0x26),
    };

    private int _rotation;
    private ImageSource? _thumbnail;
    private double _tileSize;

    public PageTile(int source, int page, bool showSource, double tileSize)
    {
        Source = source;
        Page = page;
        ShowSource = showSource;
        _tileSize = tileSize;
        SourceBadge = SourceLetter(source);
        SourceBrush = new SolidColorBrush(SourceColors[source % SourceColors.Length]);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Source { get; }
    public int Page { get; }
    public bool ShowSource { get; }
    public string SourceBadge { get; }
    public Brush SourceBrush { get; }
    public string Label => ShowSource
        ? $"{SourceBadge} · {UiText.Format(UiText.OrganizerPageLabel, Page + 1)}"
        : UiText.Format(UiText.OrganizerPageLabel, Page + 1);
    public string AccessibleName => Rotation == 0 ? Label : $"{Label}, {Rotation}°";

    /// <summary>Extra clockwise rotation chosen in the organizer (0, 90, 180, 270).</summary>
    public int Rotation
    {
        get => _rotation;
        set { _rotation = OrganizePagesParameter.NormalizeRotation(value); Changed(nameof(Rotation)); Changed(nameof(AccessibleName)); }
    }

    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set { _thumbnail = value; Changed(nameof(Thumbnail)); Changed(nameof(IsLoading)); }
    }

    public bool IsLoading => _thumbnail == null;

    public double TileSize
    {
        get => _tileSize;
        set { _tileSize = value; Changed(nameof(TileSize)); }
    }


    public static string SourceLetter(int index) => index < 26 ? ((char)('A' + index)).ToString() : (index + 1).ToString();

    private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Visual page organizer: shows all pages of the selected PDFs as thumbnails; the user drags to
/// reorder, rotates and removes pages, then saves a new PDF through
/// <see cref="OrganizePagesPdfAction"/> (never overwriting the originals).
/// </summary>
public sealed partial class PageOrganizerWindow : Window
{
    private const int MaxPages = 2000;

    private readonly IAction _action;
    private readonly ContextSnapshot _snapshot;
    private readonly IReadOnlyList<SelectedFile> _sources;
    private readonly CancellationTokenSource _closing = new();
    private readonly List<PdfThumbnailSource> _thumbnailSources = new();
    private List<PageTile> _original = new();
    private IReadOnlyList<string> _outputs = Array.Empty<string>();

    public ObservableCollection<PageTile> Pages { get; } = new();

    public PageOrganizerWindow(IAction action, ContextSnapshot snapshot)
    {
        _action = action;
        _snapshot = snapshot;
        var files = snapshot.Content is FileSelection selection
            ? selection.Files.Where(f => !f.IsDirectory && string.Equals(f.Extension, ".pdf", StringComparison.OrdinalIgnoreCase))
            : Enumerable.Empty<SelectedFile>();
        _sources = NaturalFileNameComparer.Sort(files);

        InitializeComponent();
        Title = UiText.OrganizerTitle;
        TitleText.Text = UiText.OrganizerTitle;
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        TitleBarIcon.Source = AppIconImage.Create(32);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 780));
        Pages.CollectionChanged += (_, _) => UpdateCount();
        Closed += (_, _) => Cleanup();
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        var token = _closing.Token;
        try
        {
            for (int s = 0; s < _sources.Count; s++)
            {
                var source = await PdfThumbnailSource.OpenAsync(_sources[s].Path!, token);
                _thumbnailSources.Add(source);
                if (_original.Count + source.PageCount > MaxPages)
                {
                    ShowLoadError(UiText.Format(UiText.OrganizerTooLarge, MaxPages));
                    return;
                }
                for (int p = 0; p < source.PageCount; p++)
                    _original.Add(new PageTile(s, p, _sources.Count > 1, ZoomSlider.Value));
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Windows.Data.Pdf cannot open password-protected files; other failures stay generic.
            ShowLoadError(error.HResult == unchecked((int)0x8007052B) || error.Message.Contains("password", StringComparison.OrdinalIgnoreCase)
                ? UiText.OrganizerProtected
                : UiText.OrganizerOpenFailed);
            return;
        }

        foreach (var tile in _original)
            Pages.Add(tile);
        BuildSourceLegend();
        UpdateCount();
        await RenderThumbnailsAsync(token);
    }

    private async Task RenderThumbnailsAsync(CancellationToken token)
    {
        uint width = (uint)Math.Round(ZoomSlider.Maximum * (Content.XamlRoot?.RasterizationScale ?? 1.5));
        foreach (var tile in _original.ToList())
        {
            if (token.IsCancellationRequested) return;
            try
            {
                using var stream = await _thumbnailSources[tile.Source].RenderAsync(tile.Page, width, token);
                var bitmap = new BitmapImage();
                await bitmap.SetSourceAsync(stream);
                tile.Thumbnail = bitmap;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                // A page that cannot be previewed still saves; show an empty card.
                tile.Thumbnail = new BitmapImage();
            }
        }
    }

    private void BuildSourceLegend()
    {
        if (_sources.Count < 2)
            return;
        for (int s = 0; s < _sources.Count; s++)
        {
            var sample = new PageTile(s, 0, true, 0);
            SourceLegend.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new Border
                    {
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(6, 1, 6, 2),
                        Background = sample.SourceBrush,
                        Child = new TextBlock { Text = sample.SourceBadge, Foreground = new SolidColorBrush(Colors.White) },
                    },
                    new TextBlock
                    {
                        Text = _sources[s].Name,
                        VerticalAlignment = VerticalAlignment.Center,
                        MaxWidth = 220,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    },
                },
            });
        }
    }

    private void ShowLoadError(string message)
    {
        LoadInfo.Message = message;
        LoadInfo.IsOpen = true;
        SaveButton.IsEnabled = false;
    }

    private void UpdateCount()
    {
        CountText.Text = UiText.Format(UiText.OrganizerPageCount, Pages.Count);
        SaveButton.IsEnabled = Pages.Count > 0 && !LoadInfo.IsOpen && !SaveProgress.IsActive;
    }

    private IEnumerable<PageTile> Targets() =>
        PagesGrid.SelectedItems.Count > 0 ? PagesGrid.SelectedItems.OfType<PageTile>().ToList() : Pages.ToList();

    private void Rotate(IEnumerable<PageTile> tiles, int degrees)
    {
        foreach (var tile in tiles)
            tile.Rotation += degrees;
        ResultInfo.IsOpen = false;
    }

    private void Remove(IEnumerable<PageTile> tiles)
    {
        foreach (var tile in tiles.ToList())
            Pages.Remove(tile);
        ResultInfo.IsOpen = false;
    }

    private void RotateLeft_Click(object sender, RoutedEventArgs e) => Rotate(Targets(), -90);
    private void RotateRight_Click(object sender, RoutedEventArgs e) => Rotate(Targets(), 90);
    private void Remove_Click(object sender, RoutedEventArgs e) => Remove(PagesGrid.SelectedItems.OfType<PageTile>());
    private void SelectAll_Click(object sender, RoutedEventArgs e) => PagesGrid.SelectAll();
    private void TileRotateLeft_Click(object sender, RoutedEventArgs e) { if ((sender as FrameworkElement)?.Tag is PageTile t) Rotate(new[] { t }, -90); }
    private void TileRotateRight_Click(object sender, RoutedEventArgs e) { if ((sender as FrameworkElement)?.Tag is PageTile t) Rotate(new[] { t }, 90); }
    private void TileRemove_Click(object sender, RoutedEventArgs e) { if ((sender as FrameworkElement)?.Tag is PageTile t) Remove(new[] { t }); }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        Pages.Clear();
        foreach (var tile in _original)
        {
            tile.Rotation = 0;
            Pages.Add(tile);
        }
        ResultInfo.IsOpen = false;
    }

    private void PagesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => ResultInfo.IsOpen = false;

    private void ZoomSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        foreach (var tile in _original)
            tile.TileSize = e.NewValue;
    }

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Delete && PagesGrid.SelectedItems.Count > 0)
        {
            e.Handled = true;
            Remove(PagesGrid.SelectedItems.OfType<PageTile>());
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (Pages.Count == 0) return;
        string arrangement = OrganizePagesParameter.Format(Pages.Select(p => (p.Source, p.Page, p.Rotation)));
        var parameters = new DictionaryParameters(new Dictionary<string, string> { ["Pages"] = arrangement });
        // The organizer may stay open longer than a palette snapshot lives; the action re-validates
        // every file at execution anyway.
        var snapshot = _snapshot with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5) };

        SaveButton.IsEnabled = false;
        SaveProgress.IsActive = true;
        ResultInfo.IsOpen = false;
        try
        {
            var receipt = await _action.ExecuteAsync(snapshot, parameters, null, _closing.Token);
            _outputs = receipt.OutputPaths;
            ResultInfo.Severity = receipt.IsSuccess ? InfoBarSeverity.Success : InfoBarSeverity.Error;
            ResultInfo.Message = receipt.IsSuccess ? UiText.OrganizerSaved : (receipt.Message ?? UiText.OrganizerSaveFailed);
            RevealButton.Visibility = receipt.IsSuccess && _outputs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            ResultInfo.IsOpen = true;
            if (receipt.IsSuccess)
                CloseButton.Content = UiText.CommonClose;
        }
        catch (OperationCanceledException)
        {
            // Window closed while saving.
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            ResultInfo.Severity = InfoBarSeverity.Error;
            ResultInfo.Message = UiText.OrganizerSaveFailed;
            RevealButton.Visibility = Visibility.Collapsed;
            ResultInfo.IsOpen = true;
        }
        finally
        {
            SaveProgress.IsActive = false;
            UpdateCount();
        }
    }

    private async void Reveal_Click(object sender, RoutedEventArgs e)
    {
        if (_outputs.Count > 0)
            await new OutputRevealer(new PowerTrove.Platform.Windows.Shell.ShellNavigator()).RevealOutputsAsync(_outputs);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Cleanup()
    {
        _closing.Cancel();
        foreach (var source in _thumbnailSources)
            source.Dispose();
        _thumbnailSources.Clear();
    }
}
