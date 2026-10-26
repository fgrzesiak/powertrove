using System.Diagnostics;
using PowerTrove.Abstractions.Office;
using PowerTrove.Pack.Office.Windows;
using Microsoft.Win32;

namespace PowerTrove.Platform.Windows.Tests;

public sealed class OfficeConverterTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    public void Dispose() => _dir.Dispose();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessfulConversion_UsesPrivateCopiesAndWaitsForStaDisposal(bool reflow)
    {
        string input = _dir.Write(reflow ? "source.pdf" : "source.rtf", "source");
        string output = _dir.File(reflow ? "out.docx" : "out.pdf");
        var session = new Session(); var factory = new Factory(session); var store = new Store(); var validator = new Validator();
        var converter = new OfficeComConverter(validator, factory, new WordReflowPreferences(store));
        if (reflow) await converter.ConvertPdfToWordAsync(input, output, CancellationToken.None);
        else await converter.ConvertToPdfAsync(input, output, CancellationToken.None);
        Assert.Equal("converted", File.ReadAllText(output)); Assert.Equal("source", File.ReadAllText(input));
        Assert.NotEqual(input, session.Input); Assert.NotEqual(output, session.Output);
        Assert.Equal(session.Input, validator.Input); Assert.True(session.Disposed);
        Assert.Equal(ApartmentState.STA, session.Apartment);
        Assert.False(Directory.Exists(Path.GetDirectoryName(session.Input)));
        Assert.False(store.Value.Exists); Assert.Equal(reflow ? 1 : 0, store.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureAndDeadline_RestorePreferenceAndNeverPublish(bool timeout)
    {
        string input = _dir.Write("source.pdf", "source"), output = _dir.File("out.docx");
        var store = new Store { Value = new RegistryPreference(true, "original", RegistryValueKind.String) };
        var session = new Session { Block = timeout, Fail = !timeout };
        var clock = new Clock();
        var converter = new OfficeComConverter(new Validator(), new Factory(session), new WordReflowPreferences(store), timeProvider: clock);
        var task = converter.ConvertPdfToWordAsync(input, output, CancellationToken.None);
        if (timeout) { await session.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); clock.Fire(); }
        var exception = await Assert.ThrowsAsync<OfficeConversionException>(() => task);
        Assert.Equal(timeout ? OfficeFailure.TimedOut : OfficeFailure.ConversionFailed, exception.Failure);
        Assert.Equal(new RegistryPreference(true, "original", RegistryValueKind.String), store.Value);
        Assert.False(File.Exists(output));
        await session.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (timeout) Assert.True(session.Aborted);
    }

    [Fact]
    public async Task Cancellation_RestoresOriginalAbsence()
    {
        string input = _dir.Write("source.pdf", "source"), output = _dir.File("out.docx");
        var store = new Store(); var session = new Session { Block = true };
        var converter = new OfficeComConverter(new Validator(), new Factory(session), new WordReflowPreferences(store));
        using var cancel = new CancellationTokenSource();
        var task = converter.ConvertPdfToWordAsync(input, output, cancel.Token);
        await session.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.False(store.Value.Exists); Assert.False(File.Exists(output));
        await session.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RejectedInput_NeverCreatesOfficeOrChangesPreference()
    {
        var session = new Session(); var factory = new Factory(session); var store = new Store();
        var converter = new OfficeComConverter(new Validator { Reject = true }, factory, new WordReflowPreferences(store));
        string input = _dir.Write("source.rtf", "source");
        await Assert.ThrowsAsync<OfficeConversionException>(() => converter.ConvertToPdfAsync(input, _dir.File("out.pdf"), CancellationToken.None));
        Assert.Equal(0, factory.Created); Assert.Equal(0, store.Writes); Assert.Empty(_dir.NewFiles(input));
    }

    [Theory]
    [InlineData(true, true, true, true, true, false)] // pre-existing process
    [InlineData(false, false, true, true, true, false)] // older process
    [InlineData(false, true, false, true, true, false)] // other session
    [InlineData(false, true, true, false, true, false)] // wrong executable
    [InlineData(false, true, true, true, false, false)] // invalid PID
    [InlineData(false, true, true, true, true, true)]
    public void Ownership_RejectsUnrelatedAndExistingProcesses(bool exists, bool newStart, bool sameSession, bool sameName, bool validPid, bool expected)
    {
        var identity = new OfficeProcessIdentity(validPid ? 42 : 0, newStart ? 20 : 5, sameSession ? 7 : 8, sameName ? "WINWORD" : "OTHER");
        Assert.Equal(expected, OfficeProcessOwnership.CanOwn(identity, exists ? new[] { identity } : Array.Empty<OfficeProcessIdentity>(), 10, 7, "WINWORD"));
    }

    [Fact]
    public void ProgIdDetection_DoesNotActivateOffice()
    {
        var ids = new List<string>();
        var factory = new OfficeAutomationFactory(resolve: id => { ids.Add(id); return id == "Word.Application" ? typeof(object) : null; }, activate: _ => throw new InvalidOperationException());
        Assert.True(factory.IsAvailable(OfficeApplication.Word)); Assert.False(factory.IsAvailable(OfficeApplication.Excel));
        Assert.Equal(new[] { "Word.Application", "Excel.Application" }, ids);
    }

    [Fact]
    public void SharedServer_IsReleasedWithoutChangingOrQuittingIt()
    {
        var app = new SharedApp(); var processes = new Processes();
        var factory = new OfficeAutomationFactory(processes, _ => typeof(SharedApp), _ => app);
        var ex = Assert.Throws<OfficeConversionException>(() => factory.Create(OfficeApplication.Word));
        Assert.Equal(OfficeFailure.Busy, ex.Failure); Assert.False(app.Changed); Assert.Empty(processes.Killed);
    }

    [Fact]
    public void ActualSession_ConfiguresSecurityBeforeOpenAndClosesWithoutSaving()
    {
        var app = new WordApp(); var processes = new OwnedProcesses();
        var factory = new OfficeAutomationFactory(processes, _ => typeof(WordApp), _ => app);
        string output = _dir.File("output.pdf");
        using (var session = factory.Create(OfficeApplication.Word)) session.Convert("private-copy.rtf", output, false, CancellationToken.None);
        Assert.True(app.Documents.SecureReadOnlyOpen); Assert.True(app.Documents.Document.ClosedWithoutSave); Assert.True(app.QuitWithoutSave);
        Assert.False(app.Options.UpdateLinksAtOpen); Assert.Empty(processes.Killed); Assert.Equal("PDF", File.ReadAllText(output));
    }

    [Fact]
    public void RegistryLease_RestoresExactKindAndValueAndIsIdempotent()
    {
        var old = new RegistryPreference(true, new byte[] { 1, 2 }, RegistryValueKind.Binary); var store = new Store { Value = old };
        var lease = new WordReflowPreferences(store).SuppressConversionWarning();
        Assert.Equal(RegistryValueKind.DWord, store.Value.Kind); lease.Dispose(); lease.Dispose();
        Assert.Equal(old, store.Value); Assert.Equal(2, store.Writes); Assert.Equal(1, store.Releases);
    }

    public sealed class SharedApp
    {
        public int Hwnd => 1;
        public bool Changed { get; private set; }
        public bool Visible { set => Changed = true; }
        public void Quit() => Changed = true;
    }
    public sealed class WordApp
    {
        public WordApp() => Documents = new WordDocuments(this);
        public int Hwnd => 42;
        public string Version => "16.0";
        public int AutomationSecurity { get; set; }
        public int DisplayAlerts { get; set; }
        public bool Visible { get; set; } = true;
        public WordOptions Options { get; } = new();
        public WordDocuments Documents { get; }
        public bool QuitWithoutSave { get; private set; }
        public void Quit(int save) => QuitWithoutSave = save == 0;
    }
    public sealed class WordOptions { public bool UpdateLinksAtOpen { get; set; } = true; }
    public sealed class WordDocuments(WordApp app)
    {
        public int Count => 0;
        public bool SecureReadOnlyOpen { get; private set; }
        public WordDocument Document { get; } = new();
        public WordDocument Open(string FileName, bool ConfirmConversions, bool ReadOnly, bool AddToRecentFiles, string PasswordDocument,
            string PasswordTemplate, string WritePasswordDocument, string WritePasswordTemplate, bool Visible, bool OpenAndRepair, bool NoEncodingDialog)
        {
            SecureReadOnlyOpen = app.AutomationSecurity == 3 && app.DisplayAlerts == 0 && !app.Visible && ReadOnly && !ConfirmConversions && !AddToRecentFiles && !Visible && NoEncodingDialog;
            if (!SecureReadOnlyOpen) throw new InvalidOperationException("Unsafe document open.");
            return Document;
        }
    }
    public sealed class WordDocument
    {
        public bool ClosedWithoutSave { get; private set; }
        public void ExportAsFixedFormat(string OutputFileName, int ExportFormat, bool OpenAfterExport)
        { Assert.Equal(17, ExportFormat); Assert.False(OpenAfterExport); File.WriteAllText(OutputFileName, "PDF"); }
        public void Close(int save) => ClosedWithoutSave = save == 0;
    }
    private sealed class OwnedProcesses : IOfficeProcessService
    {
        public List<OfficeProcessIdentity> Killed { get; } = new();
        public IReadOnlyList<OfficeProcessIdentity> Snapshot(string name) => Array.Empty<OfficeProcessIdentity>();
        public OfficeProcessIdentity? FromWindow(nint window) => new(42, DateTime.UtcNow.AddSeconds(1).Ticks, Process.GetCurrentProcess().SessionId, "WINWORD");
        public void Kill(OfficeProcessIdentity identity) => Killed.Add(identity);
    }
    private sealed class Processes : IOfficeProcessService
    {
        private readonly OfficeProcessIdentity _identity = new(42, 1, Process.GetCurrentProcess().SessionId, "WINWORD");
        public List<OfficeProcessIdentity> Killed { get; } = new();
        public IReadOnlyList<OfficeProcessIdentity> Snapshot(string name) => new[] { _identity };
        public OfficeProcessIdentity? FromWindow(nint window) => _identity;
        public void Kill(OfficeProcessIdentity identity) => Killed.Add(identity);
    }
    private sealed class Validator : IOfficeInputValidator
    {
        public bool Reject { get; init; }
        public string? Input { get; private set; }
        public Task ValidateAsync(string path, OfficeApplication app, bool reflow, CancellationToken ct)
        { Input = path; if (Reject) throw new OfficeConversionException(OfficeFailure.UnsafeMacros); return Task.CompletedTask; }
    }
    private sealed class Factory : IOfficeAutomationFactory
    {
        private readonly Session _session;
        public Factory(Session session) => _session = session;
        public int Created { get; private set; }
        public bool IsAvailable(OfficeApplication application) => true;
        public IOfficeAutomationSession Create(OfficeApplication application) { Created++; return _session; }
    }
    private sealed class Session : IOfficeAutomationSession
    {
        private readonly ManualResetEventSlim _release = new();
        public bool Block { get; init; }
        public bool Fail { get; init; }
        public bool Aborted { get; private set; }
        public bool Disposed { get; private set; }
        public string? Input { get; private set; }
        public string? Output { get; private set; }
        public ApartmentState Apartment { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Convert(string input, string output, bool reflow, CancellationToken ct)
        {
            Input = input; Output = output; Apartment = Thread.CurrentThread.GetApartmentState(); Started.TrySetResult();
            if (Block && !_release.Wait(TimeSpan.FromSeconds(5))) throw new InvalidOperationException("Fake worker was not stopped.");
            ct.ThrowIfCancellationRequested();
            if (Fail) throw new OfficeConversionException(OfficeFailure.ConversionFailed);
            File.WriteAllText(output, "converted");
        }
        public void Abort() { Aborted = true; try { _release.Set(); } catch (ObjectDisposedException) { } }
        public void Dispose() { Disposed = true; Finished.TrySetResult(); _release.Dispose(); }
    }
    private sealed class Store : IWordPreferenceStore
    {
        public RegistryPreference Value { get; set; } = new(false, null, RegistryValueKind.None);
        public int Writes { get; private set; }
        public int Releases { get; private set; }
        public IDisposable Enter() => new Release(() => Releases++);
        public RegistryPreference Read() => Value;
        public void Write(object value, RegistryValueKind kind) { Writes++; Value = new(true, value, kind); }
        public void Delete() => Value = new(false, null, RegistryValueKind.None);
        private sealed class Release(Action action) : IDisposable { public void Dispose() => action(); }
    }
    private sealed class Clock : TimeProvider
    {
        private TimerCallback? _callback;
        private object? _state;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { _callback = callback; _state = state; return new Timer(); }
        public void Fire() => _callback!(_state);
        private sealed class Timer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
