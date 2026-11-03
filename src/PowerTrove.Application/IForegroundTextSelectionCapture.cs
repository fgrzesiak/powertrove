namespace PowerTrove.Application;

/// <summary>
/// Optional, read-only capture while the original application still owns focus.
/// Implementations must never simulate keystrokes, change the clipboard, or retain text.
/// A null result means unsupported/no selection and allows normal context resolution.
/// </summary>
public interface IForegroundTextSelectionCapture
{
    ValueTask<string?> CaptureAsync(SourceWindowIdentity source, CancellationToken cancellationToken);
}

/// <summary>Keep specialized Explorer and Excel selections authoritative.</summary>
public static class ForegroundTextSelectionPolicy
{
    public const int MaximumCharacters = 65536;
    public static readonly TimeSpan MaximumWait = TimeSpan.FromMilliseconds(450);

    public static bool ShouldCapture(SourceWindowIdentity source) =>
        source.WindowHandle != IntPtr.Zero && source.ProcessId > 0 &&
        !string.Equals(source.ProcessName, "explorer", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(source.ProcessName, "excel", StringComparison.OrdinalIgnoreCase);

    public static bool IsValid(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > MaximumCharacters || text.IndexOf('\0') >= 0)
            return false;
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]))
            {
                if (++i >= text.Length || !char.IsLowSurrogate(text[i])) return false;
            }
            else if (char.IsLowSurrogate(text[i])) return false;
        }
        return true;
    }
}
