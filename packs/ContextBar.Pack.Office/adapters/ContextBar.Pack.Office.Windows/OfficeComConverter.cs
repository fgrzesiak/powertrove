using ContextBar.Abstractions.Office;

namespace ContextBar.Pack.Office.Windows;

/// <summary>Bounded, serialized Office automation. Late STA completion can only touch private copies.</summary>
public sealed class OfficeComConverter : IOfficeConverter
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private readonly IOfficeInputValidator _validator;
    private readonly IOfficeAutomationFactory _factory;
    private readonly IWordReflowPreferences _preferences;
    private readonly TimeSpan _timeout;
    private readonly TimeProvider _timeProvider;

    public OfficeComConverter(IOfficeInputValidator validator, IOfficeAutomationFactory? factory = null, IWordReflowPreferences? preferences = null, TimeSpan? timeout = null, TimeProvider? timeProvider = null)
    {
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _factory = factory ?? new OfficeAutomationFactory();
        _preferences = preferences ?? new WordReflowPreferences();
        _timeout = timeout ?? TimeSpan.FromSeconds(120);
        _timeProvider = timeProvider ?? TimeProvider.System;
        if (_timeout <= TimeSpan.Zero || _timeout > TimeSpan.FromSeconds(120)) throw new ArgumentOutOfRangeException(nameof(timeout));
    }
    public bool IsAvailable(OfficeApplication application) => _factory.IsAvailable(application);
    public Task ConvertToPdfAsync(string sourcePath, string destinationPdfPath, CancellationToken cancellationToken) =>
        RunAsync(sourcePath, destinationPdfPath, OfficeFormats.ApplicationFor(sourcePath) ?? throw new OfficeConversionException(OfficeFailure.InvalidDocument), false, cancellationToken);
    public Task ConvertPdfToWordAsync(string sourcePath, string destinationDocxPath, CancellationToken cancellationToken) =>
        RunAsync(sourcePath, destinationDocxPath, OfficeApplication.Word, true, cancellationToken);

    private async Task RunAsync(string source, string destination, OfficeApplication application, bool reflow, CancellationToken callerToken)
    {
        callerToken.ThrowIfCancellationRequested();
        if (!IsAvailable(application)) throw new OfficeConversionException(OfficeFailure.MissingApplication);
        using var deadline = new CancellationTokenSource(_timeout, _timeProvider);
        using var stopped = CancellationTokenSource.CreateLinkedTokenSource(callerToken, deadline.Token);
        bool gateOwned = false, deferredCleanup = false;
        string work = Path.Combine(Path.GetTempPath(), "ContextBar.Office", Guid.NewGuid().ToString("N"));
        IDisposable? preference = null;
        Task? worker = null;
        var sessionState = new SessionState();
        try
        {
            await Gate.WaitAsync(stopped.Token).ConfigureAwait(false); gateOwned = true;
            Directory.CreateDirectory(work);
            string privateInput = Path.Combine(work, "input" + Path.GetExtension(source).ToLowerInvariant());
            string privateOutput = Path.Combine(work, reflow ? "output.docx" : "output.pdf");
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, true))
            {
                if (input.Length > 64L * 1024 * 1024) throw new OfficeConversionException(OfficeFailure.InvalidDocument);
                await using var copy = new FileStream(privateInput, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, true);
                await input.CopyToAsync(copy, stopped.Token).ConfigureAwait(false);
            }
            await _validator.ValidateAsync(privateInput, application, reflow, stopped.Token).ConfigureAwait(false);
            if (reflow) preference = _preferences.SuppressConversionWarning();
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var workerToken = stopped.Token;
            var thread = new Thread(() =>
            {
                try
                {
                    workerToken.ThrowIfCancellationRequested();
                    var session = _factory.Create(application);
                    try { sessionState.Publish(session); workerToken.ThrowIfCancellationRequested(); session.Convert(privateInput, privateOutput, reflow, workerToken); }
                    finally { try { session.Dispose(); } finally { sessionState.Clear(); } }
                    completion.TrySetResult();
                }
                catch (OperationCanceledException) { completion.TrySetCanceled(); }
                catch (OfficeConversionException ex) { completion.TrySetException(ex); }
                catch (Exception) { completion.TrySetException(new OfficeConversionException(OfficeFailure.ConversionFailed)); }
            }) { IsBackground = true, Name = "ContextBar Office conversion" };
            thread.SetApartmentState(ApartmentState.STA);
            worker = completion.Task;
            using var registration = stopped.Token.Register(sessionState.Stop);
            try { thread.Start(); }
            catch { completion.TrySetResult(); throw; }
            await worker.WaitAsync(stopped.Token).ConfigureAwait(false);
            stopped.Token.ThrowIfCancellationRequested();
            if (!File.Exists(privateOutput) || new FileInfo(privateOutput).Length == 0) throw new OfficeConversionException(OfficeFailure.ConversionFailed);
            // Copy to the shared writer's staged path only after successful STA cleanup.
            await using var result = new FileStream(privateOutput, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, true);
            await using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, true);
            await result.CopyToAsync(target, stopped.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !callerToken.IsCancellationRequested)
        { throw new OfficeConversionException(OfficeFailure.TimedOut); }
        finally
        {
            // Registry restoration belongs to the bounded controller, not a potentially blocked COM call.
            try { preference?.Dispose(); }
            finally
            {
                if (worker is { IsCompleted: false })
                {
                    sessionState.Stop();
                    deferredCleanup = true;
                    _ = worker.ContinueWith(t => { _ = t.Exception; try { DeleteWork(work); } finally { Gate.Release(); } }, CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
                if (!deferredCleanup) { DeleteWork(work); if (gateOwned) Gate.Release(); }
            }
        }
    }
    private static void DeleteWork(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    private sealed class SessionState
    {
        private readonly object _gate = new();
        private IOfficeAutomationSession? _session;
        private bool _stopped;
        public void Publish(IOfficeAutomationSession session) { lock (_gate) { _session = session; if (_stopped) session.Abort(); } }
        public void Stop() { lock (_gate) { _stopped = true; _session?.Abort(); } }
        public void Clear() { lock (_gate) _session = null; }
    }
}
