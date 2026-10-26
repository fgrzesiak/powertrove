using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using PowerTrove.Core.Activation;
using Microsoft.Win32.SafeHandles;

namespace PowerTrove.Platform.Windows.Activation;

/// <summary>One mutex owner thread per elected process; async continuations never release another thread's mutex.</summary>
public sealed class WindowsHostElector : IHostElector
{
    private readonly string _mutexName;
    private readonly string _pipeName;
    private readonly string _sid;
    private readonly int _session;
    private readonly object _gate = new();
    private readonly ManualResetEventSlim _stop = new();
    private readonly CancellationTokenSource _lifetime = new();
    private Thread? _ownerThread;
    private bool _isHost;
    private bool _disposed;
    private int _listening;

    public WindowsHostElector(string instanceKey = "PowerTrove")
    {
        if (instanceKey.Length is 0 or > 64 || !instanceKey.All(char.IsAsciiLetterOrDigit))
            throw new ArgumentException("Invalid instance key.", nameof(instanceKey));
        using var identity = WindowsIdentity.GetCurrent();
        _sid = identity.User?.Value ?? throw new InvalidOperationException("User identity is unavailable.");
        using var process = Process.GetCurrentProcess();
        _session = process.SessionId;
        string key = $"{instanceKey}_{_sid}_{_session}";
        _mutexName = $@"Local\{key}";
        _pipeName = key;
    }

    public bool Elect()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_isHost) return true;
            _ownerThread?.Join();
            using var ready = new ManualResetEventSlim();
            Exception? failure = null;
            _ownerThread = new Thread(() =>
            {
                try
                {
                    using var mutex = CreateRestrictedMutex();
                    uint result = WaitForSingleObject(mutex, 0);
                    if (result == 0xFFFFFFFF) throw new Win32Exception(Marshal.GetLastWin32Error());
                    _isHost = result is 0 or 0x80; // WAIT_OBJECT_0 or WAIT_ABANDONED
                    ready.Set();
                    if (_isHost)
                    {
                        try { _stop.Wait(); }
                        finally { ReleaseMutex(mutex); }
                    }
                }
                catch (Exception ex) { failure = ex; ready.Set(); }
            }) { IsBackground = true, Name = "PowerTrove host election" };
            _ownerThread.Start();
            ready.Wait();
            if (failure is not null) throw new InvalidOperationException("Host election is unavailable.", failure);
            return _isHost;
        }
    }

    private SafeWaitHandle CreateRestrictedMutex()
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor($"D:P(A;;GA;;;{_sid})", 1, out var descriptor, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
            var mutex = CreateMutexEx(ref security, _mutexName, 0, 0x00100001); // SYNCHRONIZE | MUTEX_MODIFY_STATE
            if (mutex.IsInvalid) { mutex.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
            return mutex;
        }
        finally { LocalFree(descriptor); }
    }

    public async Task StartListeningAsync(Func<ActivationMessage, CancellationToken, Task<bool>> receive, CancellationToken cancellationToken)
    {
        if (!_isHost) throw new InvalidOperationException("Host has not been elected.");
        if (Interlocked.Exchange(ref _listening, 1) != 0) throw new InvalidOperationException("Host is already listening.");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        while (!lifetime.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance,
                    4096, 4096);
                await server.WaitForConnectionAsync(lifetime.Token).ConfigureAwait(false);
                using var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                request.CancelAfter(TimeSpan.FromSeconds(3));
                if (!SameSession(server.SafePipeHandle, client: true)) continue;
                var message = await ActivationProtocol.ReadAsync(server, request.Token).ConfigureAwait(false);
                bool accepted = message is not null && await receive(message, request.Token).WaitAsync(request.Token).ConfigureAwait(false);
                await server.WriteAsync(new byte[] { accepted ? (byte)1 : (byte)0 }, request.Token).ConfigureAwait(false);
                await server.FlushAsync(request.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException
                or InvalidDataException or Win32Exception or InvalidOperationException)
            {
                // Privacy-safe: no payloads, paths or raw exception text. A stalled/malformed client cannot hold the listener.
                try { await Task.Delay(50, lifetime.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    public async Task<bool> SendMessageAsync(ActivationMessage message, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await client.ConnectAsync(timeout.Token).ConfigureAwait(false);
            if (!SameSession(client.SafePipeHandle, client: false)) return false;
            await ActivationProtocol.WriteAsync(client, message, timeout.Token).ConfigureAwait(false);
            byte[] ack = new byte[1];
            await client.ReadExactlyAsync(ack, timeout.Token).ConfigureAwait(false);
            return ack[0] == 1;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception) { return false; }
    }

    private bool SameSession(SafePipeHandle pipe, bool client)
    {
        uint pid;
        bool success = client ? GetNamedPipeClientProcessId(pipe, out pid) : GetNamedPipeServerProcessId(pipe, out pid);
        if (!success || pid == 0 || pid > int.MaxValue) return false;
        // Remote pipe clients cannot provide a matching local computer identity.
        if (client)
        {
            var computer = new System.Text.StringBuilder(256);
            bool retrieved = GetNamedPipeClientComputerName(pipe, computer, checked((uint)computer.Capacity * 2));
            // A genuinely local pipe reports ERROR_PIPE_LOCAL (229), not a computer name.
            // A returned name identifies an SMB client; refuse it even if the name resembles ours.
            if (retrieved || Marshal.GetLastWin32Error() != 229) return false;
        }
        try
        {
            using var process = Process.GetProcessById((int)pid);
            if (process.SessionId != _session) return false;
            if (!client) AllowSetForegroundWindow(pid);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception) { return false; }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _lifetime.Cancel();
            _stop.Set();
            _ownerThread?.Join();
            _isHost = false;
            _stop.Dispose();
            // The listener owns its linked token until it has observed cancellation.
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes { public int Length; public IntPtr Descriptor; public int InheritHandle; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string text, uint revision, out IntPtr descriptor, out uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateMutexExW")]
    private static extern SafeWaitHandle CreateMutexEx(ref SecurityAttributes attributes, string name, uint flags, uint access);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ReleaseMutex(SafeWaitHandle handle);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr pointer);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AllowSetForegroundWindow(uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetNamedPipeClientComputerNameW")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetNamedPipeClientComputerName(SafePipeHandle pipe, System.Text.StringBuilder name, uint size);
}
