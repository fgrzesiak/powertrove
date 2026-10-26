using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace PowerTrove.Core.Tests.Fakes;

public class FakeProvidersAndActionsTests
{
    [Fact]
    public async Task FakeContextProvider_ReturnsConfiguredSelection()
    {
        // Arrange
        var provider = Fixtures.ProviderFixtures.CreateValidFileProvider();
        var source = new SourceWindowIdentity((nint)1234, 5678, "explorer", DateTimeOffset.UtcNow);

        // Act
        var snapshot = await provider.CaptureAsync(Guid.NewGuid(), source, CancellationToken.None);

        // Assert
        Assert.Equal(ContextStatus.Available, snapshot.Status);
        var fileSelection = Assert.IsType<FileSelection>(snapshot.Content);
        Assert.Single(fileSelection.Files);
        Assert.Equal("test1.txt", fileSelection.Files[0].Name);
    }

    [Fact]
    public async Task FakeContextProvider_HandlesCancellationCorrectly()
    {
        // Arrange
        var provider = new FakeContextProvider
        {
            // Far longer than the cancellation delay, so a busy machine cannot finish the work first.
            CaptureDelay = TimeSpan.FromSeconds(30)
        };
        var source = new SourceWindowIdentity((nint)1234, 5678, "explorer", DateTimeOffset.UtcNow);
        var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await provider.CaptureAsync(Guid.NewGuid(), source, cts.Token);
        });
    }

    [Fact]
    public async Task FakeAction_MatchAsync_ReturnsConfiguredAvailability()
    {
        // Arrange
        var action = new FakeAction
        {
            MatchResult = new ActionAvailability(IsAvailable: false, Reason: "Test reason")
        };
        var snapshot = new ContextSnapshot(Guid.NewGuid(), new SourceWindowIdentity((nint)123, 456, "test", DateTimeOffset.UtcNow), ContextStatus.Available, new NoContent(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5), "ProviderId", null);

        // Act
        var availability = await action.MatchAsync(snapshot, CancellationToken.None);

        // Assert
        Assert.False(availability.IsAvailable);
        Assert.Equal("Test reason", availability.Reason);
    }

    [Fact]
    public async Task FakeAction_ExecuteAsync_HandlesCancellationCorrectly()
    {
        // Arrange
        var action = new FakeAction
        {
            // Far longer than the cancellation delay, so a busy machine cannot finish the work first.
            ExecuteDelay = TimeSpan.FromSeconds(30)
        };
        var snapshot = new ContextSnapshot(Guid.NewGuid(), new SourceWindowIdentity((nint)123, 456, "test", DateTimeOffset.UtcNow), ContextStatus.Available, new NoContent(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5), "ProviderId", null);
        var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await action.ExecuteAsync(snapshot, new EmptyParameters(), null, cts.Token);
        });
    }

    [Fact]
    public async Task FakeAction_ExecuteAsync_ReportsExecutionProgress()
    {
        // Arrange
        var action = new FakeAction
        {
            ExecuteDelay = TimeSpan.FromMilliseconds(100)
        };
        var snapshot = new ContextSnapshot(Guid.NewGuid(), new SourceWindowIdentity((nint)123, 456, "test", DateTimeOffset.UtcNow), ContextStatus.Available, new NoContent(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5), "ProviderId", null);

        var progressReports = new List<ActionProgress>();
        var progress = new Progress<ActionProgress>(p => progressReports.Add(p));

        // Act
        var result = await action.ExecuteAsync(snapshot, new EmptyParameters(), progress, CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal(1, action.ExecuteCallCount);
        Assert.NotEmpty(progressReports);
        Assert.Contains(progressReports, p => p.Percentage > 0 && p.Percentage <= 100);
    }
}
