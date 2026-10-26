using System;
using System.Runtime.InteropServices;

namespace PowerTrove.Platform.Windows.Input;

internal sealed class WindowsNativeHotkeyInterop : INativeHotkeyInterop
{
    private readonly string _className = "PowerTroveHotkeyMsgWindow_" + Guid.NewGuid().ToString("N");
    private IntPtr _hwnd;
    private IntPtr _instance;
    private bool _classRegistered;
    private WinProc? _winProc;
    private GCHandle _callbackRoot;

    public IntPtr CreateMessageWindow(Action<int, IntPtr> processMessage)
    {
        if (!OperatingSystem.IsWindows()) return IntPtr.Zero;
        if (_classRegistered) throw new InvalidOperationException("A message window is already owned by this adapter.");

        _instance = GetModuleHandle(IntPtr.Zero);
        _winProc = (hwnd, msg, wParam, lParam) =>
        {
            processMessage(unchecked((int)msg), wParam);
            return DefWindowProc(hwnd, msg, wParam, lParam);
        };
        // Native class registration outlives ordinary managed references. Keep the
        // callback (and its owner) rooted until both window and class are released.
        _callbackRoot = GCHandle.Alloc(_winProc);
        var windowClass = new WNDCLASS
        {
            lpszClassName = _className,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_winProc),
            hInstance = _instance
        };
        if (RegisterClass(ref windowClass) == 0)
        {
            _callbackRoot.Free();
            _winProc = null;
            return IntPtr.Zero;
        }
        _classRegistered = true;
        _hwnd = CreateWindowEx(0, _className, "", 0, 0, 0, 0, 0,
            new IntPtr(-3) /* HWND_MESSAGE */, IntPtr.Zero, _instance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
            DestroyMessageWindow(); // Release the class even when window creation fails.
        return _hwnd;
    }

    public bool DestroyMessageWindow()
    {
        // If destruction fails (e.g. on a different thread), retain the callback
        // root and class so native code cannot call a collected delegate. Allow retry.
        if (_hwnd != IntPtr.Zero)
        {
            if (!DestroyWindow(_hwnd)) return false;
            _hwnd = IntPtr.Zero;
        }
        if (_classRegistered)
        {
            if (!UnregisterClass(_className, _instance)) return false;
            _classRegistered = false;
            _callbackRoot.Free();
            _winProc = null;
        }
        return true;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    bool INativeHotkeyInterop.RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk)
        => RegisterHotKey(hWnd, id, fsModifiers, vk);

    bool INativeHotkeyInterop.UnregisterHotKey(IntPtr hWnd, int id)
        => UnregisterHotKey(hWnd, id);

    public int GetLastError() => Marshal.GetLastWin32Error();

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WinProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASS
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClass(ref WNDCLASS lpWndClass);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool UnregisterClass(string lpClassName, IntPtr hInstance);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(uint dwExStyle, string lpClassName,
        string lpWindowName, uint dwStyle, int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(IntPtr moduleName);
}
