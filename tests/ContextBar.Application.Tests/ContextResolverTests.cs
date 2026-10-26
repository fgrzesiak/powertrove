using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ContextBar.Application;
using ContextBar.Core;
using Xunit;

namespace ContextBar.Application.Tests;

public class ContextResolverTests
{
    private class DummyProvider : IContextProvider
    {
        public string Id { get; }
        public bool WillCapture { get; set; }
        public ContextSnapshot Snapshot { get; set; }
        public Exception? ExceptionToThrow { get; set; }

        public DummyProvider(string id)
        {
            Id = id;
            Snapshot = new ContextSnapshot(Guid.Empty, new SourceWindowIdentity(IntPtr.Zero, 0, null, DateTimeOffset.UtcNow), ContextStatus.Available, new NoContent(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, id, null);
        }

        public ValueTask<bool> CanCaptureAsync(SourceWindowIdentity source, CancellationToken cancellationToken)
        {
            if (ExceptionToThrow != null) throw ExceptionToThrow;
            return new ValueTask<bool>(WillCapture);
        }

        public ValueTask<ContextSnapshot> CaptureAsync(Guid invocationId, SourceWindowIdentity source, CancellationToken cancellationToken)
        {
            if (ExceptionToThrow != null) throw ExceptionToThrow;
            return new ValueTask<ContextSnapshot>(Snapshot);
        }
    }

    [Fact]
    public async Task ResolveContextAsync_NoProviders_ReturnsUnsupported()
    {
        var resolver = new ContextResolver(new List<IContextProvider>());
        var identity = new SourceWindowIdentity(IntPtr.Zero, 1, "test", DateTimeOffset.UtcNow);

        var result = await resolver.ResolveContextAsync(Guid.NewGuid(), identity, CancellationToken.None);

        Assert.Equal(ContextStatus.Unsupported, result.Status);
    }

    [Fact]
    public async Task ResolveContextAsync_FirstProviderMatches_ReturnsSnapshot()
    {
        var provider1 = new DummyProvider("p1") { WillCapture = false };
        var provider2 = new DummyProvider("p2") { WillCapture = true };

        var resolver = new ContextResolver(new List<IContextProvider> { provider1, provider2 });
        var identity = new SourceWindowIdentity(IntPtr.Zero, 1, "test", DateTimeOffset.UtcNow);

        var result = await resolver.ResolveContextAsync(Guid.NewGuid(), identity, CancellationToken.None);

        Assert.Equal("p2", result.ProviderId);
        Assert.Equal(ContextStatus.Available, result.Status);
    }

    [Fact]
    public async Task ResolveContextAsync_ProviderThrowsException_ReturnsFailedSnapshot()
    {
        var provider1 = new DummyProvider("p1") { ExceptionToThrow = new InvalidOperationException("Boom") };
        var provider2 = new DummyProvider("p2") { WillCapture = true }; // Should not be reached

        var resolver = new ContextResolver(new List<IContextProvider> { provider1, provider2 });
        var identity = new SourceWindowIdentity(IntPtr.Zero, 1, "test", DateTimeOffset.UtcNow);

        var result = await resolver.ResolveContextAsync(Guid.NewGuid(), identity, CancellationToken.None);

        Assert.Equal(ContextStatus.Failed, result.Status);
        Assert.Equal("p1", result.ProviderId);
        Assert.Contains("Boom", result.UserMessage);
    }

    [Fact]
    public async Task ResolveContextAsync_ProviderThrowsOperationCanceledException_Rethrows()
    {
        var provider = new DummyProvider("p1") { ExceptionToThrow = new OperationCanceledException() };
        var resolver = new ContextResolver(new List<IContextProvider> { provider });
        var identity = new SourceWindowIdentity(IntPtr.Zero, 1, "test", DateTimeOffset.UtcNow);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            resolver.ResolveContextAsync(Guid.NewGuid(), identity, CancellationToken.None).AsTask());
    }
}
