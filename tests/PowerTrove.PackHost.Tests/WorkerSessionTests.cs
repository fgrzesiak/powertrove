using System.Threading.Channels;
using PowerTrove.Abstractions.Packs;
using PowerTrove.Core.Extensibility.PackProtocol;
using PowerTrove.PackHost.TestPack;

namespace PowerTrove.PackHost.Tests;

public sealed class WorkerSessionTests
{
    [Fact]
    public async Task HandshakePreviewAndExecuteReturnOnlyStagedOutputs()
    {
        await using var host = new Harness();
        await host.StartAsync();
        var previewId = Guid.NewGuid();
        await host.Send(new PreviewMessage(previewId, "contextbar.fixture.copy", host.Selection, new Dictionary<string, string>(), host.Expires));
        var preview = await host.Receive<PreviewResultMessage>();
        Assert.Equal(previewId, preview.RequestId);
        Assert.False(preview.IsBlocked);
        Assert.True(preview.RequiresConfirmation);
        Assert.False(File.Exists(host.OutputPath));
        var execute = host.Execute();
        await host.Send(execute);
        var result = await host.Receive<ResultMessage>();
        Assert.Equal(execute.RequestId, result.RequestId);
        Assert.True(result.Receipt.IsSuccess);
        var output = Assert.Single(result.Outputs);
        Assert.Equal(Path.GetDirectoryName(host.InputPath), output.IntendedDirectory);
        Assert.Equal("fixture-copy.txt", output.IntendedName);
        Assert.Equal(output.Path, Assert.Single(result.Receipt.OutputPaths));
        Assert.False(Path.IsPathFullyQualified(output.Path));
        Assert.Equal("generated fixture output", File.ReadAllText(Path.Combine(execute.StagingDirectory, output.Path)));
        Assert.False(File.Exists(host.OutputPath));
        Assert.Equal("original", File.ReadAllText(host.InputPath));
        Assert.All(host.Progress, p => Assert.Null(p.Message));
    }

    [Fact]
    public async Task FakeClockHeartbeatAndCancellationKeepTheProtocolResponsive()
    {
        TinyPackFactory.Reset();
        var clock = new ManualClock();
        await using var host = new Harness(clock);
        await host.StartAsync();
        var execute = host.Execute("contextbar.fixture.wait");
        await host.Send(execute);
        await TinyPackFactory.Started.Task.WaitAsync(host.Token);
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(execute.RequestId, (await host.Receive<HeartbeatMessage>()).RequestId);
        // A second request is rejected while the first is running, without blocking cancellation.
        await host.Send(host.Execute());
        Assert.Equal("invalidRequest", (await host.Receive<ErrorMessage>()).Code);
        await host.Send(new CancelMessage(execute.RequestId));
        var result = await host.Receive<ResultMessage>();
        Assert.True(result.Receipt.IsCanceled);
        Assert.Empty(result.Outputs);
        Assert.False(File.Exists(host.OutputPath));
        // The same worker is reusable after a canceled request.
        await host.Send(host.Execute());
        Assert.True((await host.Receive<ResultMessage>()).Receipt.IsSuccess);
    }

    [Fact]
    public async Task ExpiredSelectionAndUnownedStageFailWithoutPathsInErrors()
    {
        await using var host = new Harness();
        await host.StartAsync();
        var request = host.Execute() with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) };
        await host.Send(request);
        var expired = await host.Receive<ErrorMessage>();
        Assert.Equal("invalidRequest", expired.Code);
        Assert.DoesNotContain(host.InputPath, expired.Message);
        await host.Send(host.Execute() with { StagingDirectory = Path.GetDirectoryName(host.InputPath)! });
        Assert.Equal("invalidRequest", (await host.Receive<ErrorMessage>()).Code);
        await host.Send(host.Execute("contextbar.fixture.missing"));
        Assert.Equal("unknownAction", (await host.Receive<ErrorMessage>()).Code);
        Assert.False(File.Exists(host.OutputPath));
    }

    [Fact]
    public async Task ManifestActionInventoryCannotBeExpandedByAFactory()
    {
        await using var host = new Harness(declaredIds: new HashSet<string> { "contextbar.fixture.copy" });
        await host.StartAsync();
        await host.Send(host.Execute());
        Assert.Equal("invalidRequest", (await host.Receive<ErrorMessage>()).Code);
        Assert.False(File.Exists(host.OutputPath));
    }

    [Fact]
    public async Task RequestsBeforeHelloAreRejectedAndShutdownCancelsActiveWork()
    {
        TinyPackFactory.Reset();
        await using var host = new Harness();
        await host.Send(host.Execute());
        Assert.Equal("invalidRequest", (await host.Receive<ErrorMessage>()).Code);
        await host.StartAsync();
        await host.Send(host.Execute("contextbar.fixture.wait"));
        await TinyPackFactory.Started.Task.WaitAsync(host.Token);
        await host.Send(new ShutdownMessage());
        await host.Completion.WaitAsync(host.Token);
        Assert.False(File.Exists(host.OutputPath));
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly TempDirectory _temp = new();
        private readonly ChannelStream _input = new(), _output = new();
        private readonly PackMessageWriter _writer;
        private readonly IAsyncEnumerator<PackMessage> _reader;
        private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(15));
        private readonly TimeProvider _clock;
        private readonly string _staging;
        public string InputPath { get; }
        public string OutputPath => Path.Combine(Path.GetDirectoryName(InputPath)!, "fixture-copy.txt");
        public FileSelectionPayload Selection { get; }
        public DateTimeOffset Expires => _clock.GetUtcNow().AddMinutes(1);
        public CancellationToken Token => _deadline.Token;
        public List<ProgressMessage> Progress { get; } = new();
        public Task Completion { get; }
        public Harness(TimeProvider? clock = null, IReadOnlySet<string>? declaredIds = null)
        {
            _clock = clock ?? TimeProvider.System;
            InputPath = _temp.Write("source.txt", "original");
            Selection = FileSelectionPayload.FromSnapshot(Snapshots.ForPaths(InputPath));
            _staging = Directory.CreateDirectory(_temp.File("staging")).FullName;
            var worker = new WorkerSession([new TinyPackFactory()], files => new PackHostServices(new Version(0, 1, 0), files),
                new ExtensionOnlyTypeVerifier(), _staging, _clock, declaredIds);
            _writer = new PackMessageWriter(_input);
            _reader = PackMessageStream.ReadAsync(_output, Token).GetAsyncEnumerator(Token);
            Completion = worker.RunAsync(_input, _output, Token);
        }
        public async Task StartAsync()
        {
            await Send(new HelloMessage(1, "0.1.0"));
            var ready = await Receive<ReadyMessage>();
            Assert.Equal("contextbar.fixture", Assert.Single(ready.Packs).Id);
        }
        public ExecuteMessage Execute(string action = "contextbar.fixture.copy") => new(Guid.NewGuid(), action, Selection,
            new Dictionary<string, string>(), Expires, Directory.CreateDirectory(Path.Combine(_staging, Guid.NewGuid().ToString("N"))).FullName);
        public Task Send(PackMessage message) => _writer.WriteAsync(message, Token);
        public async Task<T> Receive<T>() where T : PackMessage
        {
            while (await _reader.MoveNextAsync().AsTask().WaitAsync(Token))
            {
                if (_reader.Current is ProgressMessage progress) { Progress.Add(progress); continue; }
                return Assert.IsType<T>(_reader.Current);
            }
            throw new InvalidOperationException("Worker stream ended before the expected response.");
        }
        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!Completion.IsCompleted) await Send(new ShutdownMessage());
                await Completion.WaitAsync(Token);
            }
            finally
            {
                _deadline.Cancel();
                await _reader.DisposeAsync();
                _writer.Dispose(); _input.Dispose(); _output.Dispose(); _deadline.Dispose(); _temp.Dispose();
            }
        }
    }

    // A live duplex pair avoids EOF implicitly canceling an action before its result arrives.
    private sealed class ChannelStream : Stream
    {
        private readonly Channel<byte[]> _channel = Channel.CreateUnbounded<byte[]>();
        private byte[]? _pending;
        private int _offset;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (buffer.IsEmpty) return 0;
            if (_pending is null)
            {
                if (!await _channel.Reader.WaitToReadAsync(token)) return 0;
                _pending = await _channel.Reader.ReadAsync(token); _offset = 0;
            }
            int count = Math.Min(buffer.Length, _pending.Length - _offset);
            _pending.AsMemory(_offset, count).CopyTo(buffer); _offset += count;
            if (_offset == _pending.Length) _pending = null;
            return count;
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default) => _channel.Writer.WriteAsync(buffer.ToArray(), token);
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken token) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { _channel.Writer.TryComplete(); base.Dispose(disposing); }
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        private readonly List<ManualTimer> _timers = new();
        public override DateTimeOffset GetUtcNow() => _now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_timers)
            {
                var timer = new ManualTimer(this, callback, state); timer.Change(dueTime, period); _timers.Add(timer); return timer;
            }
        }
        public void Advance(TimeSpan elapsed)
        {
            ManualTimer[] timers;
            lock (_timers) { _now += elapsed; timers = _timers.ToArray(); }
            foreach (var timer in timers) timer.Fire(_now);
        }
        private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            private DateTimeOffset? _due;
            private TimeSpan _period;
            private bool _disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (clock._timers)
                {
                    if (_disposed) return false;
                    _due = dueTime == Timeout.InfiniteTimeSpan ? null : clock._now + dueTime; _period = period; return true;
                }
            }
            public void Fire(DateTimeOffset now)
            {
                lock (clock._timers)
                {
                    if (_disposed || _due is null || _due > now) return;
                    _due = _period == Timeout.InfiniteTimeSpan ? null : now + _period;
                }
                callback(state);
            }
            public void Dispose() { lock (clock._timers) { _disposed = true; _due = null; } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
