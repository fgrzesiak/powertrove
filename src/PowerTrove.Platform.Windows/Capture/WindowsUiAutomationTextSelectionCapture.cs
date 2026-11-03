using System.Runtime.InteropServices;
using System.Text;
using Interop.UIAutomationClient;
using PowerTrove.Application;

namespace PowerTrove.Platform.Windows.Capture;

/// <summary>Best-effort selected-text capture before the palette takes keyboard focus.</summary>
public sealed class WindowsUiAutomationTextSelectionCapture : IForegroundTextSelectionCapture
{
    private static readonly SemaphoreSlim CaptureSlot = new(1, 1);
    private const int MaximumAncestry = 64;
    private const int TextPatternId = 10014; // UIA_TextPatternId

    public async ValueTask<string?> CaptureAsync(SourceWindowIdentity source, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows() || !ForegroundTextSelectionPolicy.ShouldCapture(source))
            return null;
        // Native providers can hang. At most one concurrent background probe is allowed.
        if (!CaptureSlot.Wait(0)) return null;
        try
        {
            var task = Task.Run(() =>
            {
                try { return Probe(source, cancellationToken); }
                finally { CaptureSlot.Release(); }
            }, CancellationToken.None);
            return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
        catch (COMException) { return null; }
        catch (InvalidOperationException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static string? Probe(SourceWindowIdentity source, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            if (!SourceStillFocused(source)) return null;
            IUIAutomation automation = new CUIAutomation8();
            var focused = automation.GetFocusedElement();
            if (focused is null) return null;
            var walker = automation.RawViewWalker;

            // Check the focused control's ancestry before requesting any selected text.
            // This also blocks password controls and text belonging to other windows.
            var chain = new List<IUIAutomationElement>();
            bool sameSource = false;
            for (IUIAutomationElement? current = focused; current is not null && chain.Count < MaximumAncestry; current = walker.GetParentElement(current))
            {
                token.ThrowIfCancellationRequested();
                if (current.CurrentIsPassword != 0) return null;
                chain.Add(current);
                nint hwnd = current.CurrentNativeWindowHandle;
                if (hwnd != IntPtr.Zero && (hwnd == source.WindowHandle ||
                    GetAncestor(hwnd, 2 /* GA_ROOT */) == source.WindowHandle))
                {
                    sameSource = true;
                    break;
                }
            }
            if (!sameSource || !SourceStillFocused(source)) return null;

            // A browser may expose TextPattern at the document ancestor rather than
            // the focused element. Never search siblings or read whole-document text.
            foreach (var element in chain)
            {
                token.ThrowIfCancellationRequested();
                if (element.GetCurrentPattern(TextPatternId) is not IUIAutomationTextPattern pattern)
                    continue;

                var ranges = pattern.GetSelection();
                if (ranges is null || ranges.Length is 0 or > 32) continue;
                var result = new StringBuilder();
                for (int index = 0; index < ranges.Length; index++)
                {
                    token.ThrowIfCancellationRequested();
                    int room = ForegroundTextSelectionPolicy.MaximumCharacters + 1 - result.Length;
                    if (room <= 0) return null;
                    string part = ranges.GetElement(index).GetText(room);
                    if (string.IsNullOrEmpty(part)) continue;
                    if (result.Length > 0) result.Append('\n');
                    result.Append(part);
                    if (result.Length > ForegroundTextSelectionPolicy.MaximumCharacters) return null;
                }
                if (!SourceStillFocused(source)) return null;
                string text = result.ToString();
                if (ForegroundTextSelectionPolicy.IsValid(text)) return text;
            }
            return null;
        }
        catch (OperationCanceledException) { return null; }
        catch (COMException) { return null; }
        catch (InvalidOperationException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (ArgumentException) { return null; }
    }

    private static bool SourceStillFocused(SourceWindowIdentity source)
    {
        if (GetForegroundWindow() != source.WindowHandle) return false;
        _ = GetWindowThreadProcessId(source.WindowHandle, out uint process);
        return process != 0 && process == (uint)source.ProcessId;
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint hwnd, uint flags);
}
