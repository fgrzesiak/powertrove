using System.Diagnostics;
using System.Runtime.InteropServices;
using PowerTrove.Abstractions.Office;

namespace PowerTrove.Pack.Office.Windows;

public sealed record OfficeProcessIdentity(int Id, long StartTimeUtcTicks, int SessionId, string Name);

public interface IOfficeProcessService
{
    IReadOnlyList<OfficeProcessIdentity> Snapshot(string name);
    OfficeProcessIdentity? FromWindow(nint window);
    void Kill(OfficeProcessIdentity identity);
}

public static class OfficeProcessOwnership
{
    public static bool CanOwn(OfficeProcessIdentity candidate, IReadOnlyList<OfficeProcessIdentity> before,
        long activationStartedUtcTicks, int sessionId, string expectedName) =>
        candidate.Id > 0 && candidate.StartTimeUtcTicks >= activationStartedUtcTicks && candidate.SessionId == sessionId &&
        string.Equals(candidate.Name, expectedName, StringComparison.OrdinalIgnoreCase) && !before.Any(p => p.Id == candidate.Id);
}

/// <summary>Never uses GetActiveObject. An existing/shared server is released without changing it.</summary>
public sealed class OfficeAutomationFactory : IOfficeAutomationFactory
{
    private readonly IOfficeProcessService _processes;
    private readonly Func<string, Type?> _resolve;
    private readonly Func<Type, object?> _activate;

    public OfficeAutomationFactory(IOfficeProcessService? processes = null, Func<string, Type?>? resolve = null, Func<Type, object?>? activate = null)
    {
        _processes = processes ?? new OfficeProcessService();
        _resolve = resolve ?? (id => OperatingSystem.IsWindows() ? Type.GetTypeFromProgID(id, false) : null);
        _activate = activate ?? Activator.CreateInstance;
    }
    private static string ProgId(OfficeApplication kind) => kind + ".Application";
    public bool IsAvailable(OfficeApplication application)
    {
        try { return _resolve(ProgId(application)) is not null; }
        catch (Exception ex) when (ex is COMException or ArgumentException or UnauthorizedAccessException) { return false; }
    }
    public IOfficeAutomationSession Create(OfficeApplication application)
    {
        string name = application switch { OfficeApplication.Word => "WINWORD", OfficeApplication.Excel => "EXCEL", _ => "POWERPNT" };
        var before = _processes.Snapshot(name);
        long started = DateTime.UtcNow.Ticks;
        var type = _resolve(ProgId(application)) ?? throw new OfficeConversionException(OfficeFailure.MissingApplication);
        object? app = _activate(type);
        if (app is null) throw new OfficeConversionException(OfficeFailure.MissingApplication);
        OfficeProcessIdentity? identity = null;
        try
        {
            dynamic automation = app;
            nint hwnd = (nint)Convert.ToInt64(automation.Hwnd, System.Globalization.CultureInfo.InvariantCulture);
            identity = _processes.FromWindow(hwnd);
            if (identity is null || !OfficeProcessOwnership.CanOwn(identity, before, started, Process.GetCurrentProcess().SessionId, name))
                throw new OfficeConversionException(OfficeFailure.Busy);
            object? documents = null;
            try
            {
                documents = application switch { OfficeApplication.Word => automation.Documents, OfficeApplication.Excel => automation.Workbooks, _ => automation.Presentations };
                if ((int)((dynamic)documents).Count != 0) throw new OfficeConversionException(OfficeFailure.Busy);
            }
            finally { Release(documents); }
            var session = new Session(application, app, identity, _processes);
            app = null;
            return session;
        }
        catch
        {
            // Even a newly started server with an unexpected document is not ours to terminate.
            // No property was changed and no selected document has been opened at this point.
            throw;
        }
        finally
        {
            Release(app);
        }
    }
    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            try { Marshal.FinalReleaseComObject(value); }
            catch (InvalidComObjectException) { }
        }
    }

    private sealed class Session : IOfficeAutomationSession
    {
        private readonly OfficeApplication _kind;
        private readonly object _application;
        private readonly OfficeProcessIdentity _identity;
        private readonly IOfficeProcessService _processes;
        private int _aborted;
        public Session(OfficeApplication kind, object application, OfficeProcessIdentity identity, IOfficeProcessService processes)
        { _kind = kind; _application = application; _identity = identity; _processes = processes; }

        public void Convert(string sourcePath, string destinationPath, bool pdfToWord, CancellationToken ct)
        {
            object? collection = null, document = null, options = null;
            dynamic app = _application;
            try
            {
                ct.ThrowIfCancellationRequested();
                if (pdfToWord && !((string)app.Version).StartsWith("16.", StringComparison.Ordinal))
                    throw new OfficeConversionException(OfficeFailure.InvalidDocument);
                // Must succeed before opening anything; never silently fall back to Office defaults.
                app.AutomationSecurity = 3; // msoAutomationSecurityForceDisable
                app.DisplayAlerts = _kind == OfficeApplication.PowerPoint ? 1 : 0; // ppAlertsNone, wdAlertsNone / false
                if (_kind != OfficeApplication.PowerPoint) app.Visible = false;
                if (_kind == OfficeApplication.Excel)
                {
                    app.EnableEvents = false;
                    app.AskToUpdateLinks = false;
                }
                if (_kind == OfficeApplication.Word)
                {
                    options = app.Options;
                    ((dynamic)options).UpdateLinksAtOpen = false;
                }
                ct.ThrowIfCancellationRequested();
                if (_kind == OfficeApplication.Word)
                {
                    collection = app.Documents;
                    document = ((dynamic)collection).Open(FileName: sourcePath, ConfirmConversions: false, ReadOnly: true, AddToRecentFiles: false,
                        PasswordDocument: "", PasswordTemplate: "", WritePasswordDocument: "", WritePasswordTemplate: "", Visible: false, OpenAndRepair: false, NoEncodingDialog: true);
                    ct.ThrowIfCancellationRequested();
                    if (pdfToWord) ((dynamic)document).SaveAs2(FileName: destinationPath, FileFormat: 16, AddToRecentFiles: false);
                    else ((dynamic)document).ExportAsFixedFormat(OutputFileName: destinationPath, ExportFormat: 17, OpenAfterExport: false);
                }
                else if (_kind == OfficeApplication.Excel)
                {
                    collection = app.Workbooks;
                    document = ((dynamic)collection).Open(Filename: sourcePath, UpdateLinks: 0, ReadOnly: true, Password: "", WriteResPassword: "",
                        IgnoreReadOnlyRecommended: true, Notify: false, AddToMru: false);
                    ct.ThrowIfCancellationRequested();
                    ((dynamic)document).ExportAsFixedFormat(Type: 0, Filename: destinationPath, OpenAfterPublish: false);
                }
                else
                {
                    collection = app.Presentations;
                    document = ((dynamic)collection).Open(FileName: sourcePath, ReadOnly: -1, Untitled: 0, WithWindow: 0);
                    ct.ThrowIfCancellationRequested();
                    ((dynamic)document).SaveAs(destinationPath, 32);
                }
                ct.ThrowIfCancellationRequested();
            }
            catch (COMException ex) when (ex.HResult == unchecked((int)0x800A1520))
            { throw new OfficeConversionException(OfficeFailure.PasswordProtected); }
            finally
            {
                if (document is not null)
                {
                    try
                    {
                        if (_kind == OfficeApplication.PowerPoint) { ((dynamic)document).Saved = -1; ((dynamic)document).Close(); }
                        else ((dynamic)document).Close(0); // wdDoNotSaveChanges / false
                    }
                    catch (Exception) { }
                }
                Release(document); Release(collection); Release(options);
            }
        }
        public void Abort()
        {
            if (Interlocked.Exchange(ref _aborted, 1) == 0) _processes.Kill(_identity);
        }
        public void Dispose()
        {
            try
            {
                if (_aborted == 0)
                {
                    dynamic app = _application;
                    if (_kind == OfficeApplication.Word) app.Quit(0);
                    else app.Quit();
                }
            }
            catch (Exception) { Abort(); }
            finally { Release(_application); }
        }
    }
}

public sealed class OfficeProcessService : IOfficeProcessService
{
    public IReadOnlyList<OfficeProcessIdentity> Snapshot(string name)
    {
        var result = new List<OfficeProcessIdentity>();
        foreach (var process in Process.GetProcessesByName(name))
        {
            using (process) { var identity = Read(process); if (identity is not null) result.Add(identity); }
        }
        return result;
    }
    public OfficeProcessIdentity? FromWindow(nint window)
    {
        if (window == 0 || GetWindowThreadProcessId(window, out uint pid) == 0 || pid == 0) return null;
        try { using var process = Process.GetProcessById(checked((int)pid)); return Read(process); }
        catch (ArgumentException) { return null; }
    }
    public void Kill(OfficeProcessIdentity identity)
    {
        try
        {
            using var process = Process.GetProcessById(identity.Id);
            if (Read(process) == identity && !process.HasExited) process.Kill(entireProcessTree: false);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
    }
    private static OfficeProcessIdentity? Read(Process process)
    {
        try { return new(process.Id, process.StartTime.ToUniversalTime().Ticks, process.SessionId, process.ProcessName); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { return null; }
    }
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
}
