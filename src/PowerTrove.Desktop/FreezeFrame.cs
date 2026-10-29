using System;
using System.Runtime.InteropServices;
using Windows.Graphics;

namespace PowerTrove.Desktop;

/// <summary>
/// Covers a screen rectangle with a still image of what it shows right now. WinUI renders a window a few
/// frames late after it moves or resizes; showing the previous picture on top for those frames hides the
/// stale and black frames in between. The cover is a layered, click-through tool window that never takes
/// focus, and it only shows pixels that were on screen anyway.
/// </summary>
internal sealed class FreezeFrame : IDisposable
{
    private IntPtr _window, _memoryDc, _bitmap, _oldBitmap;

    private FreezeFrame() { }

    /// <summary>
    /// Captures <paramref name="bounds"/> and shows the capture on top; null if that fails. Owned by
    /// <paramref name="owner"/>, the still always stays above it, even when the owner is raised or activated.
    /// </summary>
    public static FreezeFrame? Show(RectInt32 bounds, IntPtr owner = default)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return null;
        var frame = new FreezeFrame();
        IntPtr screen = GetDC(IntPtr.Zero);
        try
        {
            frame._memoryDc = CreateCompatibleDC(screen);
            frame._bitmap = CreateCompatibleBitmap(screen, bounds.Width, bounds.Height);
            if (frame._memoryDc == IntPtr.Zero || frame._bitmap == IntPtr.Zero) { frame.Dispose(); return null; }
            frame._oldBitmap = SelectObject(frame._memoryDc, frame._bitmap);
            if (!BitBlt(frame._memoryDc, 0, 0, bounds.Width, bounds.Height, screen, bounds.X, bounds.Y, SrcCopy)) { frame.Dispose(); return null; }

            frame._window = CreateWindowEx(ExLayered | ExTransparent | ExToolWindow | ExNoActivate | ExTopmost,
                ClassName(), string.Empty, WsPopup, bounds.X, bounds.Y, bounds.Width, bounds.Height,
                owner, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
            if (frame._window == IntPtr.Zero) { frame.Dispose(); return null; }
            var position = new NativePoint { X = bounds.X, Y = bounds.Y };
            var size = new NativeSize { Width = bounds.Width, Height = bounds.Height };
            var source = new NativePoint();
            var blend = new BlendFunction { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 0 };
            if (!UpdateLayeredWindow(frame._window, screen, ref position, ref size, frame._memoryDc, ref source, 0, ref blend, UlwAlpha))
            { frame.Dispose(); return null; }
            ShowWindow(frame._window, SwShowNoActivate);
            // The cover must be on screen before the caller moves the window underneath it.
            DwmFlush();
            return frame;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    public void Dispose()
    {
        if (_window != IntPtr.Zero) { DestroyWindow(_window); _window = IntPtr.Zero; }
        if (_memoryDc != IntPtr.Zero)
        {
            if (_oldBitmap != IntPtr.Zero) SelectObject(_memoryDc, _oldBitmap);
            DeleteDC(_memoryDc);
            _memoryDc = IntPtr.Zero;
        }
        if (_bitmap != IntPtr.Zero) { DeleteObject(_bitmap); _bitmap = IntPtr.Zero; }
    }

    private static string? _className;
    private static WndProc? _wndProc;

    private static string ClassName()
    {
        if (_className is not null) return _className;
        _wndProc = DefWindowProc;
        var windowClass = new WndClass
        {
            WndProcPointer = Marshal.GetFunctionPointerForDelegate(_wndProc),
            Instance = GetModuleHandle(null),
            ClassName = "PowerTrove.FreezeFrame",
        };
        RegisterClass(ref windowClass);
        return _className = windowClass.ClassName;
    }

    private const int SrcCopy = 0x00CC0020, SwShowNoActivate = 4, UlwAlpha = 0x2;
    private const uint WsPopup = 0x80000000;
    private const uint ExLayered = 0x00080000, ExTransparent = 0x00000020, ExToolWindow = 0x00000080,
        ExNoActivate = 0x08000000, ExTopmost = 0x00000008;

    private delegate IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeSize { public int Width, Height; }
    [StructLayout(LayoutKind.Sequential)] private struct BlendFunction { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClass
    {
        public uint Style;
        public IntPtr WndProcPointer;
        public int ClassExtra, WindowExtra;
        public IntPtr Instance, Icon, Cursor, Background;
        public string? MenuName;
        public string ClassName;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr gdiObject);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dest, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, int rop);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr gdiObject);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr screenDc, ref NativePoint position, ref NativeSize size,
        IntPtr sourceDc, ref NativePoint sourcePosition, int colorKey, ref BlendFunction blend, int flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClass(ref WndClass windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? moduleName);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
}
