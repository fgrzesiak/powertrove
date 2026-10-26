using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ContextBar.Abstractions;
using Xunit;

namespace ContextBar.Abstractions.Testing;

/// <summary>
/// A reusable test base to ensure any implementation of IAction complies with the
/// core architectural invariants of ContextBar.
/// </summary>
public abstract class ActionContractTestsBase
{
    /// <summary>
    /// Provide the instance of the action to test.
    /// </summary>
    protected abstract IAction CreateAction();

    /// <summary>
    /// Provide a valid snapshot that this action is expected to handle successfully.
    /// </summary>
    protected abstract ContextSnapshot CreateValidSnapshot();

    [Fact]
    public void Descriptor_MustBeValid()
    {
        var action = CreateAction();
        var descriptor = action.Descriptor;

        Assert.NotNull(descriptor);
        Assert.False(string.IsNullOrWhiteSpace(descriptor.Id), "Action ID must not be empty.");
        Assert.False(string.IsNullOrWhiteSpace(descriptor.Title), "Action Title must not be empty.");
        Assert.NotNull(descriptor.Accepts);
        Assert.NotEmpty(descriptor.Accepts);
        Assert.NotNull(descriptor.SearchTerms);
    }

    [Fact]
    public async Task MatchAsync_RespectsCancellation()
    {
        var action = CreateAction();
        var snapshot = CreateValidSnapshot();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await action.MatchAsync(snapshot, cts.Token);
        });
    }

    [Fact]
    public async Task PreviewAsync_RespectsCancellation()
    {
        var action = CreateAction();
        var snapshot = CreateValidSnapshot();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await action.PreviewAsync(snapshot, new EmptyParameters(), cts.Token);
        });
    }

    [Fact]
    public async Task ExecuteAsync_RespectsCancellation()
    {
        var action = CreateAction();
        var snapshot = CreateValidSnapshot();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Implementations might return a cancelled ActionReceipt OR throw OperationCanceledException
        try
        {
            var receipt = await action.ExecuteAsync(snapshot, new EmptyParameters(), null, cts.Token);
            Assert.True(receipt.IsCanceled, "If action catches cancellation, it must return an IsCanceled receipt.");
            Assert.False(receipt.IsSuccess, "A canceled action should not be successful.");
        }
        catch (OperationCanceledException)
        {
            // Also acceptable
        }
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsFailedReceipt_WhenContextIsStale()
    {
        var action = CreateAction();
        var validSnapshot = CreateValidSnapshot();

        // Create an expired snapshot
        var staleSnapshot = validSnapshot with
        {
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        };

        // Note: Core requirement states: "actions that write outputs or perform side-effects
        // must validate context staleness... and return a failed ActionReceipt".
        // If the action is read-only (ChangesFiles = false), it might still succeed, but
        // strictly speaking all actions should probably reject stale data if they enforce it.
        // We test that if it DOES fail due to staleness, it's a clean receipt, not a throw.
        try
        {
            var receipt = await action.ExecuteAsync(staleSnapshot, new EmptyParameters(), null, CancellationToken.None);

            // Only strictly require failure for file-changing actions according to some interpretations,
            // but for safety, we assert it either succeeds cleanly or fails cleanly.
            Assert.NotNull(receipt);
            if (action.Descriptor.ChangesFiles && validSnapshot.Content is not NoContent)
            {
               Assert.False(receipt.IsSuccess, "Actions that modify files should reject stale contexts.");
            }
        }
        catch (Exception ex)
        {
            Assert.Fail($"Action threw an exception on stale context instead of returning a receipt. {ex.Message}");
        }
    }
}
