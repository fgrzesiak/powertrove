using PowerTrove.Application.Localization;

namespace PowerTrove.Desktop;

public sealed partial class OcrReaderView
{
    private async Task SaveAsync()
    {
        if (IsClosed || _snapshot.IsStale || !_saveText.IsEnabled) return;
        _saveText.IsEnabled = false;
        _status.Text = T("Saving");
        try
        {
            string? saved = await _exportTextFile(_detectedLanguage, _closing.Token);
            if (IsClosed) return;
            _status.Text = saved is null ? T("SaveFailed") : T("Saved");
            if (saved is not null)
            {
                // Saving bypasses the palette's result card, so the reader offers its "show in Explorer".
                _savedPath = saved;
                _reveal.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!IsClosed) _status.Text = T("SaveFailed"); }
        finally { if (!IsClosed) _saveText.IsEnabled = _copyAll.IsEnabled && !_snapshot.IsStale; }
    }

    private void Reveal()
    {
        if (_savedPath is null || !File.Exists(_savedPath)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{_savedPath}\"",
                UseShellExecute = true
            });
        }
        catch
        {
            // Explorer may be unavailable; the file is still saved.
        }
    }

    /// <summary>The language's own name for an OCR tag such as "de-DE"; the tag when Windows has no culture for it.</summary>
    private static string LanguageName(string tag)
    {
        try { return UiLanguages.DisplayName(tag); }
        catch (System.Globalization.CultureNotFoundException) { return tag; }
    }
}
