using System;
using System.Threading;
using System.Threading.Tasks;
using ContextBar.Abstractions;
using Xunit;

namespace ContextBar.Abstractions.Testing;

/// <summary>
/// A reusable test base to ensure any implementation of IContextProvider complies with the
/// core architectural invariants of ContextBar.
/// </summary>
public abstract class ProviderContractTestsBase
{
    /// <summary>
    /// Provide the instance of the provider to test.
    /// </summary>
    protected abstract IContextProvider CreateProvider();

    /// <summary>
    /// Provide a source identity that this provider is expected to be able to capture.
    /// </summary>
    protected abstract SourceWindowIdentity CreateSupportedSource();

    /// <summary>
    /// Provide a source identity that this provider should reject.
    /// </summary>
    protected abstract SourceWindowIdentity CreateUnsupportedSource();

    [Fact]
    public void Id_MustNotBeEmpty()
    {
        var provider = CreateProvider();
        Assert.False(string.IsNullOrWhiteSpace(provider.Id), "Provider ID must not be empty.");
    }

    [Fact]
    public async Task CanCaptureAsync_ReturnsTrue_ForSupportedSource()
    {
        var provider = CreateProvider();
        var source = CreateSupportedSource();

        var canCapture = await provider.CanCaptureAsync(source, CancellationToken.None);

        Assert.True(canCapture, "Provider should be able to capture a supported source.");
    }

    [Fact]
    public async Task CanCaptureAsync_ReturnsFalse_ForUnsupportedSource()
    {
        var provider = CreateProvider();
        var source = CreateUnsupportedSource();

        var canCapture = await provider.CanCaptureAsync(source, CancellationToken.None);

        Assert.False(canCapture, "Provider should not claim it can capture an unsupported source.");
    }

    [Fact]
    public async Task CaptureAsync_ReturnsUnsupportedStatus_ForUnsupportedSource()
    {
        var provider = CreateProvider();
        var source = CreateUnsupportedSource();
        var invocationId = Guid.NewGuid();

        var snapshot = await provider.CaptureAsync(invocationId, source, CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.Equal(ContextStatus.Unsupported, snapshot.Status);
        Assert.IsType<NoContent>(snapshot.Content);
        Assert.Equal(provider.Id, snapshot.ProviderId);
    }

    [Fact]
    public async Task CaptureAsync_ReturnsAvailableStatus_ForSupportedSource()
    {
        var provider = CreateProvider();
        var source = CreateSupportedSource();
        var invocationId = Guid.NewGuid();

        var snapshot = await provider.CaptureAsync(invocationId, source, CancellationToken.None);

        Assert.NotNull(snapshot);
        // It could also return Empty, Denied, etc. based on dynamic state, but for a mock supported source, it should be Available or at least not Unsupported simply due to the app.
        Assert.NotEqual(ContextStatus.Unsupported, snapshot.Status);
        Assert.Equal(provider.Id, snapshot.ProviderId);
        Assert.Equal(invocationId, snapshot.InvocationId);
        Assert.Equal(source, snapshot.Source);

        // Ensure expiration is in the future
        Assert.True(snapshot.ExpiresAt > snapshot.CapturedAt, "Snapshot expiration must be strictly after capture time.");
    }

    [Fact]
    public async Task CanCaptureAsync_RespectsCancellation()
    {
        var provider = CreateProvider();
        var source = CreateSupportedSource();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await provider.CanCaptureAsync(source, cts.Token);
        });
    }

    [Fact]
    public async Task CaptureAsync_RespectsCancellation()
    {
        var provider = CreateProvider();
        var source = CreateSupportedSource();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Implementations can either throw or return a status of Canceled
        try
        {
            var snapshot = await provider.CaptureAsync(Guid.NewGuid(), source, cts.Token);
            Assert.Equal(ContextStatus.Canceled, snapshot.Status);
        }
        catch (OperationCanceledException)
        {
            // Allowed behavior
        }
    }
}
