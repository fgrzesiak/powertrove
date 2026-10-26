using ContextBar.Core.Activation;
using ContextBar.Platform.Windows.Activation;

namespace ContextBar.Platform.Windows.Tests.Activation;

public class WindowsHostElectorTests
{
    [Fact]
    public async Task ConcurrentElectionHasOneOwnerAndDisposePermitsRecoveryAcrossThreads()
    {
        if (!OperatingSystem.IsWindows()) return;
        string key = "CBTest" + Guid.NewGuid().ToString("N");
        var electors = Enumerable.Range(0, 8).Select(_ => new WindowsHostElector(key)).ToArray();
        try
        {
            var results = await Task.WhenAll(electors.Select(e => Task.Run(e.Elect)));
            int owner = Array.FindIndex(results, result => result);
            Assert.Single(results.Where(result => result));
            await Task.Run(electors[owner].Dispose);
            int next = (owner + 1) % electors.Length;
            Assert.True(await Task.Run(electors[next].Elect));
        }
        finally { foreach (var elector in electors) elector.Dispose(); }
    }

    [Fact]
    public async Task SecondaryRequiresAcknowledgementAndListenerStopsOnDispose()
    {
        if (!OperatingSystem.IsWindows()) return;
        string key = "CBTest" + Guid.NewGuid().ToString("N");
        using var host = new WindowsHostElector(key);
        using var client = new WindowsHostElector(key);
        Assert.True(host.Elect());
        Assert.False(client.Elect());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        int received = 0;
        var listening = host.StartListeningAsync((message, token) =>
        {
            Assert.IsType<ActivatePaletteMessage>(message);
            Interlocked.Increment(ref received);
            return Task.FromResult(true);
        }, timeout.Token);
        Assert.True(await client.SendMessageAsync(new ActivatePaletteMessage(), timeout.Token));
        Assert.Equal(1, received);
        host.Dispose();
        await listening.WaitAsync(timeout.Token);
    }

    [Fact]
    public async Task RejectedActivationIsNotAcknowledgedAndListenerHandlesTheNextRequest()
    {
        if (!OperatingSystem.IsWindows()) return;
        string key = "CBTest" + Guid.NewGuid().ToString("N");
        using var host = new WindowsHostElector(key);
        using var client = new WindowsHostElector(key);
        Assert.True(host.Elect());
        Assert.False(client.Elect());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        int received = 0;
        var listening = host.StartListeningAsync((_, _) =>
            Task.FromResult(Interlocked.Increment(ref received) > 1), timeout.Token);
        try
        {
            Assert.False(await client.SendMessageAsync(new ActivatePaletteMessage(), timeout.Token));
            Assert.True(await client.SendMessageAsync(new ActivatePaletteMessage(), timeout.Token));
            Assert.Equal(2, received);
        }
        finally
        {
            host.Dispose();
            await listening.WaitAsync(timeout.Token);
        }
    }

    [Fact]
    public async Task DifferentInstanceNamespacesAreIndependent()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var one = new WindowsHostElector("CBTest" + Guid.NewGuid().ToString("N"));
        using var two = new WindowsHostElector("CBTest" + Guid.NewGuid().ToString("N"));
        Assert.True(await Task.Run(one.Elect));
        Assert.True(await Task.Run(two.Elect));
    }
}
