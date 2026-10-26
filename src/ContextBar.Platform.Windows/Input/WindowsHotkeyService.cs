using System;
using ContextBar.Core.Input;

namespace ContextBar.Platform.Windows.Input;

// Construct, register and dispose on the thread that owns the message pump.
public sealed class WindowsHotkeyService : IHotkeyService
{
    private readonly INativeHotkeyInterop _interop;
    private readonly IntPtr _hwnd;
    private bool _isRegistered;
    private bool _disposed;
    private HotkeyConfiguration? _currentConfig;
    private int _hotkeyId = 1;
    private int? _pendingReleaseId;
    private const int WM_HOTKEY = 0x0312;
    private const int ERROR_HOTKEY_ALREADY_REGISTERED = 1409;

    public event EventHandler? HotkeyTriggered;

    public WindowsHotkeyService() : this(new WindowsNativeHotkeyInterop())
    {
    }

    public WindowsHotkeyService(INativeHotkeyInterop interop)
    {
        _interop = interop ?? throw new ArgumentNullException(nameof(interop));
        _hwnd = _interop.CreateMessageWindow(ProcessMessage);
    }

    public RegisterHotkeyResult Register(HotkeyConfiguration configuration)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_hwnd == IntPtr.Zero)
            return RegisterHotkeyResult.InitializationFailed;

        // Retry any failed candidate cleanup before reserving another shortcut.
        if (_pendingReleaseId is int pendingId)
        {
            if (!_interop.UnregisterHotKey(_hwnd, pendingId))
                return RegisterHotkeyResult.Error;
            _pendingReleaseId = null;
        }

        if (_isRegistered && _currentConfig == configuration)
            return RegisterHotkeyResult.Success;

        uint fsModifiers = 0;
        if (configuration.Modifiers.HasFlag(KeyModifiers.Alt)) fsModifiers |= 0x0001;
        if (configuration.Modifiers.HasFlag(KeyModifiers.Control)) fsModifiers |= 0x0002;
        if (configuration.Modifiers.HasFlag(KeyModifiers.Shift)) fsModifiers |= 0x0004;
        if (configuration.Modifiers.HasFlag(KeyModifiers.Windows)) fsModifiers |= 0x0008;
        fsModifiers |= 0x4000; // MOD_NOREPEAT

        // Keep the working registration until the replacement is reserved.
        int candidateId = _isRegistered ? 3 - _hotkeyId : _hotkeyId;
        if (!_interop.RegisterHotKey(_hwnd, candidateId, fsModifiers, configuration.Key))
        {
            return _interop.GetLastError() == ERROR_HOTKEY_ALREADY_REGISTERED
                ? RegisterHotkeyResult.Conflict
                : RegisterHotkeyResult.Error;
        }

        if (_isRegistered && !_interop.UnregisterHotKey(_hwnd, _hotkeyId))
        {
            // The original remains active. Track the candidate if rollback also
            // fails so Unregister/Dispose can retry releasing both identifiers.
            if (!_interop.UnregisterHotKey(_hwnd, candidateId))
                _pendingReleaseId = candidateId;
            return RegisterHotkeyResult.Error;
        }

        _hotkeyId = candidateId;
        _isRegistered = true;
        _currentConfig = configuration;
        return RegisterHotkeyResult.Success;
    }

    public void Unregister()
    {
        if (_pendingReleaseId is int pendingId)
        {
            ReleaseRegistration(pendingId);
            _pendingReleaseId = null;
        }
        if (_isRegistered)
        {
            ReleaseRegistration(_hotkeyId);
            _isRegistered = false;
            _currentConfig = null;
        }
    }

    private void ReleaseRegistration(int id)
    {
        if (!_interop.UnregisterHotKey(_hwnd, id))
            throw new InvalidOperationException(
                $"Could not release the global shortcut (Windows error {_interop.GetLastError()}). Retry on its owning thread.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        Unregister();
        if (!_interop.DestroyMessageWindow())
            throw new InvalidOperationException("Could not release the hotkey message window. Dispose on its owning thread.");
        _disposed = true;
    }

    public void ProcessMessage(int msg, IntPtr wParam)
    {
        if (!_disposed && msg == WM_HOTKEY && wParam == new IntPtr(_hotkeyId))
            HotkeyTriggered?.Invoke(this, EventArgs.Empty);
    }
}
