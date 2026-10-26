using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using PowerTrove.Application.Packs;
using Microsoft.Win32.SafeHandles;

namespace PowerTrove.Platform.Windows.Packs;

/// <summary>Owns the worker and its kill-on-close, process-memory-limited Windows Job Object.</summary>
public sealed class WindowsPackWorkerProcessFactory : IPackWorkerProcessFactory
{
    private readonly string _executable;
    private readonly int _parentPid;
    public WindowsPackWorkerProcessFactory(string executable, int? parentPid = null)
    { _executable = Path.GetFullPath(executable); _parentPid = parentPid ?? Environment.ProcessId; }

    public Task<IPackWorkerProcess> StartAsync(PackWorkerGroup group, string workDirectory, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        cancellationToken.ThrowIfCancellationRequested();
        var info = new ProcessStartInfo(_executable)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(_executable)! };
        foreach (string directory in group.PackDirectories) { info.ArgumentList.Add("--pack-dir"); info.ArgumentList.Add(directory); }
        foreach (string hash in group.ManifestHashes ?? []) { info.ArgumentList.Add("--manifest-sha256"); info.ArgumentList.Add(hash); }
        info.ArgumentList.Add("--parent-pid"); info.ArgumentList.Add(_parentPid.ToString(CultureInfo.InvariantCulture));
        info.ArgumentList.Add("--work-root"); info.ArgumentList.Add(workDirectory);
        info.ArgumentList.Add("--ui-language"); info.ArgumentList.Add(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de" ? "de" : "en");
        SafeFileHandle? job = null; Process? process = null;
        try
        {
            job = CreateJobObject(IntPtr.Zero, null);
            if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            var limits = new JobExtendedLimitInformation
            {
                BasicLimitInformation = new JobBasicLimitInformation { LimitFlags = 0x00002000 | 0x00000100 },
                ProcessMemoryLimit = new UIntPtr(2UL * 1024 * 1024 * 1024)
            };
            if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<JobExtendedLimitInformation>()))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            process = Process.Start(info) ?? throw new IOException("Worker could not be started.");
            if (!AssignProcessToJobObject(job, process.Handle)) throw new Win32Exception(Marshal.GetLastWin32Error());
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IPackWorkerProcess>(new WindowsWorker(process, job));
        }
        catch
        {
            job?.Dispose();
            if (process is not null)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                process.Dispose();
            }
            throw;
        }
    }

    private sealed class WindowsWorker : IPackWorkerProcess
    {
        private readonly Process _process;
        private readonly SafeFileHandle _job;
        private readonly Task _stderr;
        private int _disposed;
        public WindowsWorker(Process process, SafeFileHandle job)
        { _process = process; _job = job; _stderr = DiscardAsync(process.StandardError.BaseStream); }
        public Stream Input => _process.StandardInput.BaseStream;
        public Stream Output => _process.StandardOutput.BaseStream;
        public async Task KillAsync(CancellationToken cancellationToken = default)
        {
            // Closing the job kills all contained descendants, even if protocol/native work is stuck.
            _job.Dispose();
            if (!_process.HasExited) await _process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        }
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { await KillAsync().ConfigureAwait(false); }
            finally
            {
                _process.StandardInput.Dispose(); _process.StandardOutput.Dispose(); _process.StandardError.Dispose();
                _process.Dispose();
                try { await _stderr.ConfigureAwait(false); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
            }
        }
        private static async Task DiscardAsync(Stream stream)
        {
            byte[] buffer = new byte[8192];
            try { while (await stream.ReadAsync(buffer).ConfigureAwait(false) != 0) { } }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobBasicLimitInformation
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)]
    private struct JobExtendedLimitInformation
    {
        public JobBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, ref JobExtendedLimitInformation information, uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
}
