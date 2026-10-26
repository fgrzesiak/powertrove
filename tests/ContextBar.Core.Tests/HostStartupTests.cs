using ContextBar.Core.Activation;

namespace ContextBar.Core.Tests;

public class HostStartupTests
{
    [Fact]
    public async Task AcknowledgedSecondaryDoesNotElectOrCreateAnotherHost()
    {
        using var elector = new Elector([false], [true]);
        Assert.Equal(HostStartupResult.Forwarded, await Run(elector));
        Assert.Equal(1, elector.Elections);
        Assert.Equal(1, elector.Sends);
    }

    [Fact]
    public async Task CrashedHostIsReplacedOnlyAfterElectionSucceeds()
    {
        using var elector = new Elector([false, true], [false]);
        Assert.Equal(HostStartupResult.Host, await Run(elector));
        Assert.Equal(2, elector.Elections);
        Assert.Equal(1, elector.Sends);
    }

    [Fact]
    public async Task UnreachableLivingHostDoesNotPermitDuplicateStartup()
    {
        using var elector = new Elector([false, false, false, false], [false, false, false]);
        Assert.Equal(HostStartupResult.Unavailable, await Run(elector));
        Assert.Equal(4, elector.Elections);
        Assert.Equal(3, elector.Sends);
    }

    [Fact]
    public async Task CancelledStartupDoesNotTryToOwnTheHost()
    {
        using var elector = new Elector([], []);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HostStartup.ResolveAsync(elector, new ActivatePaletteMessage(), cancelled.Token));
        Assert.Equal(0, elector.Elections);
    }

    private static Task<HostStartupResult> Run(Elector elector) =>
        HostStartup.ResolveAsync(elector, new ActivatePaletteMessage(), default, _ => Task.CompletedTask);

    private sealed class Elector(bool[] elections, bool[] sends) : IHostElector
    {
        public int Elections { get; private set; }
        public int Sends { get; private set; }
        public bool Elect() => elections[Elections++];
        public Task<bool> SendMessageAsync(ActivationMessage message, CancellationToken token) => Task.FromResult(sends[Sends++]);
        public Task StartListeningAsync(Func<ActivationMessage, CancellationToken, Task<bool>> receive, CancellationToken token) => throw new NotSupportedException();
        public void Dispose() { }
    }
}
