using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace PowerTrove.Platform.Windows.Displays;

/// <summary>A color picked from the screen.</summary>
public readonly record struct ScreenColor(byte R, byte G, byte B);

/// <summary>
/// Picks one pixel color anywhere on the desktop. It captures all monitors once, covers them with an invisible
/// click-catching window and shows a small magnifier at the cursor. A left click picks the pixel under the cursor
/// from the capture; Esc or a right click cancels. Runs on the calling UI thread's message loop and keeps no
/// pixels after it returns.
/// </summary>
public sealed class ScreenColorPicker
{
    private const int Loupe = 9, LoupeScale = 12, LoupeSize = Loupe * LoupeScale, LabelHeight = 24;

    private IntPtr _overlay, _loupe, _captureDc, _captureBitmap, _captureOld;
    private int _left, _top, _width, _height;
    private TaskCompletionSource<ScreenColor?>? _result;
    private WndProc? _proc;

    /// <summary>Lets the user pick a color; null when canceled or when the screen cannot be captured.</summary>
    public Task<ScreenColor?> PickAsync()
    {
        _result = new TaskCompletionSource<ScreenColor?>();
        try
        {
            if (!Start()) Finish(null);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Finish(null);
        }
        return _result.Task;
    }

    private bool Start()
    {
        _left = GetSystemMetrics(SmXVirtualScreen); _top = GetSystemMetrics(SmYVirtualScreen);
        _width = GetSystemMetrics(SmCxVirtualScreen); _height = GetSystemMetrics(SmCyVirtualScreen);
        IntPtr screen = GetDC(IntPtr.Zero);
        try
        {
            _captureDc = CreateCompatibleDC(screen);
            _captureBitmap = CreateCompatibleBitmap(screen, _width, _height);
            if (_captureDc == IntPtr.Zero || _captureBitmap == IntPtr.Zero) return false;
            _captureOld = SelectObject(_captureDc, _captureBitmap);
            if (!BitBlt(_captureDc, 0, 0, _width, _height, screen, _left, _top, SrcCopy | CaptureBlt)) return false;
        }
        finally { ReleaseDC(IntPtr.Zero, screen); }

        _proc = WindowProc;
        var windowClass = new WndClass
        {
            WndProcPointer = Marshal.GetFunctionPointerForDelegate(_proc),
            Instance = GetModuleHandle(null),
            Cursor = LoadCursor(IntPtr.Zero, IdcCross),
            ClassName = "PowerTrove.ScreenColorPicker." + Environment.TickCount64,
        };
        if (RegisterClass(ref windowClass) == 0) return false;

        // Almost transparent: the desktop stays visible, but the window still receives every click.
        _overlay = CreateWindowEx(ExLayered | ExToolWindow | ExTopmost, windowClass.ClassName, string.Empty, WsPopup,
            _left, _top, _width, _height, IntPtr.Zero, IntPtr.Zero, windowClass.Instance, IntPtr.Zero);
        _loupe = CreateWindowEx(ExLayered | ExTransparent | ExToolWindow | ExTopmost | ExNoActivate, windowClass.ClassName, string.Empty,
            WsPopup, 0, 0, LoupeSize, LoupeSize + LabelHeight, IntPtr.Zero, IntPtr.Zero, windowClass.Instance, IntPtr.Zero);
        if (_overlay == IntPtr.Zero || _loupe == IntPtr.Zero) return false;
        SetLayeredWindowAttributes(_overlay, 0, 1, LwaAlpha);
        ShowWindow(_overlay, SwShow);
        SetForegroundWindow(_overlay);
        SetFocus(_overlay);
        SetCapture(_overlay);
        if (GetCursorPos(out var cursor)) UpdateLoupe(cursor.X, cursor.Y);
        return true;
    }

    private IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (hwnd == _overlay)
        {
            switch (message)
            {
                case WmMouseMove:
                    if (GetCursorPos(out var moved)) UpdateLoupe(moved.X, moved.Y);
                    return IntPtr.Zero;
                case WmLButtonUp:
                    Finish(GetCursorPos(out var point) ? ColorAt(point.X, point.Y) : null);
                    return IntPtr.Zero;
                case WmRButtonUp:
                    Finish(null);
                    return IntPtr.Zero;
                case WmKeyDown when (int)wParam == VkEscape:
                    Finish(null);
                    return IntPtr.Zero;
                case WmCancelMode:
                case WmCaptureChanged when lParam != _overlay:
                    Finish(null);
                    break;
            }
        }
        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    private ScreenColor? ColorAt(int screenX, int screenY)
    {
        if (_captureDc == IntPtr.Zero) return null;
        uint value = GetPixel(_captureDc, screenX - _left, screenY - _top);
        if (value == ClrInvalid) return null;
        return new ScreenColor((byte)(value & 0xFF), (byte)((value >> 8) & 0xFF), (byte)((value >> 16) & 0xFF));
    }

    /// <summary>Draws the pixels around the cursor, enlarged, with the center marked and its hex value below.</summary>
    private void UpdateLoupe(int screenX, int screenY)
    {
        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr dc = CreateCompatibleDC(screen);
        IntPtr bitmap = CreateCompatibleBitmap(screen, LoupeSize, LoupeSize + LabelHeight);
        IntPtr old = SelectObject(dc, bitmap);
        try
        {
            SetStretchBltMode(dc, ColorOnColor);
            StretchBlt(dc, 0, 0, LoupeSize, LoupeSize, _captureDc, screenX - _left - Loupe / 2, screenY - _top - Loupe / 2, Loupe, Loupe, SrcCopy);
            var color = ColorAt(screenX, screenY);
            IntPtr frame = CreateSolidBrush(0x00FFFFFF);
            var center = new NativeRect { Left = Loupe / 2 * LoupeScale, Top = Loupe / 2 * LoupeScale, Right = (Loupe / 2 + 1) * LoupeScale, Bottom = (Loupe / 2 + 1) * LoupeScale };
            FrameRect(dc, ref center, frame);
            var label = new NativeRect { Left = 0, Top = LoupeSize, Right = LoupeSize, Bottom = LoupeSize + LabelHeight };
            IntPtr background = CreateSolidBrush(0x00202020);
            FillRect(dc, ref label, background);
            var border = new NativeRect { Left = 0, Top = 0, Right = LoupeSize, Bottom = LoupeSize + LabelHeight };
            FrameRect(dc, ref border, frame);
            DeleteObject(background);
            DeleteObject(frame);
            if (color is { } c)
            {
                SetBkMode(dc, Transparent);
                SetTextColor(dc, 0x00FFFFFF);
                string text = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
                DrawText(dc, text, text.Length, ref label, DtCenter | DtVCenter | DtSingleLine);
            }
            // Below right of the cursor, flipped to the other side near the virtual screen's edges.
            int x = screenX + 20, y = screenY + 20;
            if (x + LoupeSize > _left + _width) x = screenX - 20 - LoupeSize;
            if (y + LoupeSize + LabelHeight > _top + _height) y = screenY - 20 - LoupeSize - LabelHeight;
            var position = new NativePoint { X = x, Y = y };
            var size = new NativeSize { Width = LoupeSize, Height = LoupeSize + LabelHeight };
            var source = new NativePoint();
            var blend = new BlendFunction { SourceConstantAlpha = 255 };
            UpdateLayeredWindow(_loupe, screen, ref position, ref size, dc, ref source, 0, ref blend, UlwAlpha);
            ShowWindow(_loupe, SwShowNoActivate);
        }
        finally
        {
            SelectObject(dc, old);
            DeleteObject(bitmap);
            DeleteDC(dc);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private void Finish(ScreenColor? color)
    {
        if (_result is null) return;
        var result = _result;
        _result = null;
        ReleaseCapture();
        if (_loupe != IntPtr.Zero) { DestroyWindow(_loupe); _loupe = IntPtr.Zero; }
        if (_overlay != IntPtr.Zero) { DestroyWindow(_overlay); _overlay = IntPtr.Zero; }
        if (_captureDc != IntPtr.Zero)
        {
            if (_captureOld != IntPtr.Zero) SelectObject(_captureDc, _captureOld);
            DeleteDC(_captureDc);
            _captureDc = IntPtr.Zero;
        }
        if (_captureBitmap != IntPtr.Zero) { DeleteObject(_captureBitmap); _captureBitmap = IntPtr.Zero; }
        result.TrySetResult(color);
    }

    private const int SmXVirtualScreen = 76, SmYVirtualScreen = 77, SmCxVirtualScreen = 78, SmCyVirtualScreen = 79;
    private const int SrcCopy = 0x00CC0020, CaptureBlt = 0x40000000, SwShow = 5, SwShowNoActivate = 4;
    private const uint WsPopup = 0x80000000, ExLayered = 0x00080000, ExTransparent = 0x00000020, ExToolWindow = 0x00000080,
        ExTopmost = 0x00000008, ExNoActivate = 0x08000000, LwaAlpha = 0x2, ClrInvalid = 0xFFFFFFFF;
    private const int UlwAlpha = 0x2, IdcCross = 32515, VkEscape = 0x1B, ColorOnColor = 3, Transparent = 1;
    private const uint DtCenter = 0x1, DtVCenter = 0x4, DtSingleLine = 0x20;
    private const uint WmMouseMove = 0x0200, WmLButtonUp = 0x0202, WmRButtonUp = 0x0205, WmKeyDown = 0x0100,
        WmCancelMode = 0x001F, WmCaptureChanged = 0x0215;

    private delegate IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeSize { public int Width, Height; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
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

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr gdiObject);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dest, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, int rop);
    [DllImport("gdi32.dll")] private static extern bool StretchBlt(IntPtr dest, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, int sourceWidth, int sourceHeight, int rop);
    [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] private static extern uint GetPixel(IntPtr dc, int x, int y);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr gdiObject);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(uint color);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] private static extern uint SetTextColor(IntPtr dc, uint color);
    [DllImport("user32.dll")] private static extern int FillRect(IntPtr dc, ref NativeRect rect, IntPtr brush);
    [DllImport("user32.dll")] private static extern int FrameRect(IntPtr dc, ref NativeRect rect, IntPtr brush);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int DrawText(IntPtr dc, string text, int count, ref NativeRect rect, uint format);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClass(ref WndClass windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr screenDc, ref NativePoint position, ref NativeSize size,
        IntPtr sourceDc, ref NativePoint sourcePosition, int colorKey, ref BlendFunction blend, int flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr LoadCursor(IntPtr instance, int cursor);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr SetCapture(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? moduleName);
}
