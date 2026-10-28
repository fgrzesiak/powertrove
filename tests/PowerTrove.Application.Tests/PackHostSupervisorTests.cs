using System.Diagnostics;
using PowerTrove.Abstractions.Files;
using PowerTrove.Abstractions.Testing;
using PowerTrove.Application.Packs;
using PowerTrove.FakePackWorker;

namespace PowerTrove.Application.Tests;

public sealed class PackHostSupervisorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReinstallationRenewsBindings_WhileOldActionReferencesCannotStartWorkers(bool startBeforeRemoval)
    {
        await using var fixture = new Fixture("success");
        var descriptor = new ActionDescriptor("powertrove.fixture.copy", "Generated copy", "Generated fixture",
            System.Collections.Frozen.FrozenSet.ToFrozenSet(new[] { ContentKind.Files }), [], true, true);
        var old = new RemoteAction(fixture.Host, fixture.Group, descriptor);
        if (startBeforeRemoval)
            Assert.False((await old.PreviewAsync(fixture.Snapshot, new EmptyParameters(), fixture.Token)).IsBlocked);
        await using (var lease = await fixture.Host.AcquireMutationAsync(fixture.Token))
            await fixture.Host.StopPackAsync("powertrove.fixture", fixture.Token);
        int previousStarts = fixture.Factory.Starts;
        Assert.True((await old.PreviewAsync(fixture.Snapshot, new EmptyParameters(), fixture.Token)).IsBlocked);
        Assert.Equal(previousStarts, fixture.Factory.Starts);
        var renewed = new RemoteAction(fixture.Host, fixture.Group, descriptor);
        Assert.False((await renewed.PreviewAsync(fixture.Snapshot, new EmptyParameters(), fixture.Token)).IsBlocked);
        Assert.Equal(previousStarts + 1, fixture.Factory.Starts);
        Assert.True((await old.PreviewAsync(fixture.Snapshot, new EmptyParameters(), fixture.Token)).IsBlocked);
    }

    [Fact]
    public async Task MaintenanceWaitsForActiveRequest_AndRetiresItsOldWorkerDefinition()
    {
        await using var fixture = new Fixture("cancel");
        using var cancel = new CancellationTokenSource();
        var started = new StartedProgress();
        var execution = fixture.Execute(cancel.Token, started);
        await started.Started.Task.WaitAsync(fixture.Token);
        var maintenance = fixture.Host.AcquireMutationAsync(fixture.Token);
        Assert.False(maintenance.IsCompleted);
        Assert.False(fixture.Factory.Last!.WasKilled);
        cancel.Cancel();
        Assert.True((await execution.WaitAsync(fixture.Token)).IsCanceled);
        await using (var lease = await maintenance.WaitAsync(fixture.Token))
            await fixture.Host.StopPackAsync("powertrove.fixture", fixture.Token);
        var stale = await fixture.Execute();
        Assert.False(stale.IsSuccess); Assert.Equal(1, fixture.Factory.Starts);
        fixture.AssertCleanStaging();
    }

    [Fact]
    public async Task RealFixtureProcessCommitsWithCollisionReplanningAndReusesTheWorker()
    {
        await using var fixture = new Fixture("success");
        var preview = await fixture.Host.PreviewAsync(fixture.Group, "powertrove.fixture.copy", fixture.Snapshot, new EmptyParameters(), fixture.Token);
        Assert.False(preview.IsBlocked);
        Assert.Equal(1, fixture.Factory.Starts);
        fixture.Temp.Write("copy.txt", "existing");
        var result = await fixture.Execute();
        Assert.True(result.IsSuccess);
        Assert.Equal(fixture.Temp.File("copy (1).txt"), Assert.Single(result.OutputPaths));
        Assert.Equal("generated", File.ReadAllText(result.OutputPaths[0]));
        Assert.Equal("existing", File.ReadAllText(fixture.Temp.File("copy.txt")));
        Assert.Equal("original", File.ReadAllText(fixture.Input));
        Assert.Equal(1, fixture.Factory.Starts);
        fixture.AssertCleanStaging();
    }

    [Fact]
    public async Task CrashReportsNoChangedFilesAndRestartWaitsForBackoff()
    {
        var clock = new TestClock();
        await using var fixture = new Fixture("crash", clock);
        var first = await fixture.Execute();
        Assert.False(first.IsSuccess);
        Assert.Equal("The tool stopped unexpectedly. No files were changed.", first.Message);
        Assert.Empty(first.OutputPaths);
        fixture.AssertCleanStaging();
        var retry = fixture.Execute();
        await clock.DelayScheduled(TimeSpan.FromSeconds(1)).WaitAsync(fixture.Token);
        Assert.Equal(1, fixture.Factory.Starts);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False((await retry.WaitAsync(fixture.Token)).IsSuccess);
        Assert.Equal(2, fixture.Factory.Starts);
        Assert.False(File.Exists(fixture.Temp.File("copy.txt")));
        fixture.AssertCleanStaging();
    }

    [Fact]
    public async Task CooperativeCancelReturnsCanceledAndRemovesStaging()
    {
        await using var fixture = new Fixture("cancel");
        using var cancel = new CancellationTokenSource();
        var started = new StartedProgress();
        var work = fixture.Execute(cancel.Token, started);
        await started.Started.Task.WaitAsync(fixture.Token);
        cancel.Cancel();
        var receipt = await work.WaitAsync(fixture.Token);
        Assert.True(receipt.IsCanceled);
        Assert.Empty(receipt.OutputPaths);
        fixture.AssertCleanStaging();
        Assert.Equal("original", File.ReadAllText(fixture.Input));
    }

    [Fact]
    public async Task IgnoredCancelKillsTheWorkerAfterFiveFakeSecondsAndCleansStaging()
    {
        var clock = new TestClock();
        await using var fixture = new Fixture("hang", clock);
        using var cancel = new CancellationTokenSource();
        var started = new StartedProgress();
        var work = fixture.Execute(cancel.Token, started);
        await started.Started.Task.WaitAsync(fixture.Token);
        cancel.Cancel();
        await clock.DelayScheduled(TimeSpan.FromSeconds(5)).WaitAsync(fixture.Token);
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.True((await work.WaitAsync(fixture.Token)).IsCanceled);
        Assert.True(fixture.Factory.Last!.WasKilled);
        fixture.AssertCleanStaging();
    }

    [Fact]
    public async Task UnauthorizedDestinationFailsBeforeAnyOutputIsCommitted()
    {
        await using var fixture = new Fixture("bad-output");
        Directory.CreateDirectory(fixture.Temp.File("unapproved"));
        var result = await fixture.Execute();
        Assert.False(result.IsSuccess);
        Assert.Empty(result.OutputPaths);
        Assert.False(File.Exists(fixture.Temp.File("unapproved/copy.txt")));
        fixture.AssertCleanStaging();
    }

    [Fact]
    public async Task StartupWithoutReadyIsKilledAtTheTenSecondDeadline()
    {
        var clock = new TestClock();
        await using var fixture = new Fixture("startup-hang", clock);
        var work = fixture.Execute();
        await clock.DelayScheduled(TimeSpan.FromSeconds(10)).WaitAsync(fixture.Token);
        await fixture.Factory.Started.Task.WaitAsync(fixture.Token);
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.False((await work.WaitAsync(fixture.Token)).IsSuccess);
        Assert.True(fixture.Factory.Last!.WasKilled);
        fixture.AssertCleanStaging();
    }

    [Fact]
    public async Task IdleWorkerIsStoppedAfterTwoFakeMinutes()
    {
        var clock = new TestClock();
        await using var fixture = new Fixture("success", clock);
        Assert.True((await fixture.Execute()).IsSuccess);
        clock.Advance(TimeSpan.FromMinutes(2));
        await fixture.Factory.Last!.Stopped.Task.WaitAsync(fixture.Token);
        Assert.True(fixture.Factory.Last.WasKilled);
        fixture.AssertCleanStaging();
    }

    [Fact]
    public async Task TableActionsMatchOnlyCapturedRanges_AndOversizedTablesNeverStartAWorker()
    {
        await using var fixture = new Fixture("success");
        var descriptor = new ActionDescriptor("powertrove.table.export-csv", "Generated export", "Generated fixture",
            System.Collections.Frozen.FrozenSet.ToFrozenSet(new[] { ContentKind.Table }), [], true, true);
        var action = new RemoteAction(fixture.Host, fixture.Group, descriptor);
        ContextSnapshot Table(int rows) => fixture.Snapshot with { Content = new TableData(Enumerable.Range(0, rows)
            .Select(_ => (IReadOnlyList<CellValue>)new CellValue[] { new TextCell("0123456789") }).ToArray(), "Excel", null) };
        Assert.True((await action.MatchAsync(Table(1), fixture.Token)).IsAvailable);
        Assert.False((await action.MatchAsync(Table(0), fixture.Token)).IsAvailable);
        Assert.False((await action.MatchAsync(fixture.Snapshot, fixture.Token)).IsAvailable);
        var preview = await action.PreviewAsync(Table(300_000), new EmptyParameters(), fixture.Token);
        Assert.True(preview.IsBlocked);
        Assert.Equal(0, fixture.Factory.Starts);
        var files = new RemoteAction(fixture.Host, fixture.Group, new ActionDescriptor("powertrove.fixture.copy", "Generated copy", "Generated fixture",
            System.Collections.Frozen.FrozenSet.ToFrozenSet(new[] { ContentKind.Files }), [], true, true));
        Assert.False((await files.PreviewAsync(fixture.Snapshot, new EmptyParameters(), fixture.Token)).IsBlocked);
    }

    [Fact]
    public async Task FailedHandshakesAreReportedAsConsecutiveStartFailures_AndASuccessResetsTheCount()
    {
        await using var fixture = new Fixture("success");
        var reports = new System.Collections.Concurrent.ConcurrentQueue<(string Version, int Failures)>();
        fixture.Host.StartFailed += (group, failures) => reports.Enqueue((group.Packs[0].Version, failures));
        // The worker answers as 0.1.0, so a group expecting a new 0.2.0 fails its handshake like a broken update.
        var broken = new PackWorkerGroup("fixture-new", fixture.Group.PackDirectories, [new("powertrove.fixture", "0.2.0")]);
        for (int i = 0; i < 2; i++)
            Assert.True((await fixture.Host.PreviewAsync(broken, "powertrove.fixture.copy", fixture.Snapshot, new EmptyParameters(), fixture.Token)).IsBlocked);
        await WaitUntil(() => reports.Count == 2, fixture.Token);
        Assert.Equal([("0.2.0", 1), ("0.2.0", 2)], reports.OrderBy(r => r.Failures).ToArray());
        Assert.False((await fixture.Host.PreviewAsync(fixture.Group, "powertrove.fixture.copy", fixture.Snapshot, new EmptyParameters(), fixture.Token)).IsBlocked);
        Assert.Equal(2, reports.Count);
    }

    private static async Task WaitUntil(Func<bool> condition, CancellationToken token)
    {
        while (!condition()) await Task.Delay(20, token);
    }

    private sealed class StartedProgress : IProgress<ActionProgress>
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Report(ActionProgress value) => Started.TrySetResult();
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public TempDirectory Temp { get; } = new();
        public FixtureFactory Factory { get; } = new();
        private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(20));
        public CancellationToken Token => _deadline.Token;
        public PackHostSupervisor Host { get; }
        public PackWorkerGroup Group { get; }
        public string Input { get; }
        public ContextSnapshot Snapshot { get; }
        public Fixture(string mode, TimeProvider? clock = null)
        {
            Input = Temp.Write("source.txt", "original"); Snapshot = Snapshots.ForPaths(Input);
            string directory = Directory.CreateDirectory(Temp.File(mode)).FullName;
            Group = new("fixture", [directory], [new("powertrove.fixture", "0.1.0")]);
            Host = new PackHostSupervisor(Factory, Temp.File("user"), clock);
        }
        public Task<ActionReceipt> Execute(CancellationToken token = default, IProgress<ActionProgress>? progress = null) =>
            Host.ExecuteAsync(Group, "powertrove.fixture.copy", Snapshot, new EmptyParameters(), progress, token.CanBeCanceled ? token : Token);
        public void AssertCleanStaging() => Assert.Empty(Directory.EnumerateFileSystemEntries(Temp.File("user/staging")));
        public async ValueTask DisposeAsync()
        {
            try { await Host.DisposeAsync(); AssertCleanStaging(); Assert.Empty(Directory.EnumerateFileSystemEntries(Temp.File("user/workers"))); }
            finally { _deadline.Dispose(); Temp.Dispose(); }
        }
    }

    private sealed class FixtureFactory : IPackWorkerProcessFactory
    {
        public int Starts { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public FixtureProcess? Last { get; private set; }
        public Task<IPackWorkerProcess> StartAsync(PackWorkerGroup group, string workDirectory, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            string testAssembly = typeof(PackHostSupervisorTests).Assembly.Location;
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true };
            // Use the test host's existing dependency graph; no build/restore command is started by these tests.
            foreach (string arg in new[] { "exec", "--runtimeconfig", Path.ChangeExtension(testAssembly, ".runtimeconfig.json"),
                "--depsfile", Path.ChangeExtension(testAssembly, ".deps.json"), typeof(Marker).Assembly.Location,
                "--pack-dir", group.PackDirectories[0] }) start.ArgumentList.Add(arg);
            var process = Process.Start(start) ?? throw new IOException("Fixture process could not start.");
            Starts++; Last = new FixtureProcess(process);
            Started.TrySetResult();
            return Task.FromResult<IPackWorkerProcess>(Last);
        }
    }
    private sealed class FixtureProcess(Process process) : IPackWorkerProcess
    {
        public bool WasKilled { get; private set; }
        public TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Stream Input => process.StandardInput.BaseStream;
        public Stream Output => process.StandardOutput.BaseStream;
        public async Task KillAsync(CancellationToken token = default)
        {
            WasKilled = true;
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(5), token);
            Stopped.TrySetResult();
        }
        public async ValueTask DisposeAsync()
        { try { await KillAsync(); } finally { process.StandardInput.Dispose(); process.StandardOutput.Dispose(); process.Dispose(); } }
    }

    internal sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        private readonly List<TestTimer> _timers = new();
        private readonly Dictionary<TimeSpan, TaskCompletionSource> _scheduled = new();
        public override DateTimeOffset GetUtcNow() { lock (_timers) return _now; }
        public Task DelayScheduled(TimeSpan delay)
        {
            lock (_timers)
            {
                if (_timers.Any(t => t.Delay == delay && t.Active)) return Task.CompletedTask;
                if (!_scheduled.TryGetValue(delay, out var signal)) _scheduled[delay] = signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
                return signal.Task;
            }
        }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_timers)
            {
                var timer = new TestTimer(this, callback, state); timer.Change(dueTime, period); _timers.Add(timer); return timer;
            }
        }
        public void Advance(TimeSpan time)
        {
            TestTimer[] timers;
            lock (_timers) { _now += time; timers = _timers.ToArray(); }
            foreach (var timer in timers) timer.Fire(_now);
        }
        private sealed class TestTimer(TestClock owner, TimerCallback callback, object? state) : ITimer
        {
            private DateTimeOffset? _due;
            private TimeSpan _period;
            public TimeSpan Delay { get; private set; }
            public bool Active => _due.HasValue;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner._timers)
                {
                    Delay = dueTime; _period = period; _due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
                    if (owner._scheduled.Remove(dueTime, out var signal)) signal.TrySetResult(); return true;
                }
            }
            public void Fire(DateTimeOffset now)
            {
                lock (owner._timers)
                {
                    if (_due is null || _due > now) return;
                    _due = _period == Timeout.InfiniteTimeSpan ? null : now + _period;
                }
                callback(state);
            }
            public void Dispose() { lock (owner._timers) _due = null; }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
