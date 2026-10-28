using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace FileKit.Html;

/// <summary>Launches installed Edge suspended, assigns its whole process tree to a
/// bounded kill-on-close job, then resumes it. Never disables Chromium's sandbox.</summary>
public sealed class WindowsHtmlBrowserLauncher : IHtmlBrowserLauncher
{
    private static string? Executable => new[] {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Edge", "Application", "msedge.exe")
    }.FirstOrDefault(File.Exists);
    public bool IsAvailable => OperatingSystem.IsWindows() && Executable is not null;
    public IHtmlBrowserProcess Start(IReadOnlyList<string> arguments)
    {
        string executable = Executable ?? throw new HtmlPrintException("The installed browser is unavailable.");
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid) { job.Dispose(); throw Failure(); }
        var limits = new ExtendedLimits {
            Basic = new BasicLimits { LimitFlags = 0x2000 | 0x200 | 0x100 | 0x4 | 0x8, PerJobUserTimeLimit = 60L * 10000000, ActiveProcessLimit = 32 },
            ProcessMemoryLimit = (nuint)(512L * 1024 * 1024), JobMemoryLimit = (nuint)(1024L * 1024 * 1024)
        };
        ProcessInformation created = default;
        try
        {
            if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>())) throw Failure();
            var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
            var command = new StringBuilder(string.Join(" ", new[] { executable }.Concat(arguments).Select(Quote)));
            if (!CreateProcess(executable, command, IntPtr.Zero, IntPtr.Zero, false, 0x4 | 0x08000000, IntPtr.Zero, Path.GetDirectoryName(executable), ref startup, out created)) throw Failure();
            if (!AssignProcessToJobObject(job, created.Process)) { TerminateProcess(created.Process, 1); throw Failure(); }
            if (ResumeThread(created.Thread) == uint.MaxValue) throw Failure();
            var process = Process.GetProcessById((int)created.ProcessId);
            return new ContainedProcess(process, job);
        }
        catch { job.Dispose(); throw; }
        finally
        {
            if (created.Thread != IntPtr.Zero) CloseHandle(created.Thread);
            if (created.Process != IntPtr.Zero) CloseHandle(created.Process);
        }
    }
    private static HtmlPrintException Failure() => new("The local browser could not be contained or started.", new Win32Exception(Marshal.GetLastWin32Error()));
    private static string Quote(string value)
    {
        var result = new StringBuilder("\""); int slashes = 0;
        foreach (char c in value)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') result.Append('\\', slashes * 2 + 1).Append('"');
            else result.Append('\\', slashes).Append(c);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
    private sealed class ContainedProcess(Process process, SafeFileHandle job) : IHtmlBrowserProcess
    {
        private bool _disposed, _exited;
        public bool HasExited { get { if (_disposed) return _exited; try { return process.HasExited; } catch (InvalidOperationException) { return true; } } }
        public void Dispose()
        {
            job.Dispose();
            try { _exited = process.WaitForExit(3000); } catch (InvalidOperationException) { _exited = true; }
            _disposed = true;
            process.Dispose();
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
    { public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags; public nuint MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit; public nuint Affinity; public uint PriorityClass, SchedulingClass; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters
    { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits
    { public BasicLimits Basic; public IoCounters Io; public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo
    {
        public int Size; public IntPtr Reserved, Desktop, Title; public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public ushort ShowWindow, Reserved2Size; public IntPtr Reserved2, StdInput, StdOutput, StdError;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation
    { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(SafeFileHandle job, int kind, ref ExtendedLimits limits, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateProcess(string application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, IntPtr environment, string? directory, ref StartupInfo startup, out ProcessInformation process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateProcess(IntPtr process, uint code);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
}
