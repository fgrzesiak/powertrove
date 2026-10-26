using System;
using System.Threading;
using System.Threading.Tasks;
using ContextBar.Core;
using ContextBar.SampleAdapters.Actions;
using ContextBar.Abstractions.Testing;
using Xunit;

namespace ContextBar.SampleAdapters.Tests;

public class CountWordsActionContractTests : ActionContractTestsBase
{
    protected override IAction CreateAction() => new CountWordsAction();

    protected override ContextSnapshot CreateValidSnapshot()
    {
        return new ContextSnapshot(
            Guid.NewGuid(),
            new SourceWindowIdentity(IntPtr.Zero, 1, "notepad.exe", DateTimeOffset.UtcNow),
            ContextStatus.Available,
            new TextSelection("Some valid text to count"),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(5),
            "sample.provider",
            null
        );
    }
}

public class ReverseTextActionContractTests : ActionContractTestsBase
{
    protected override IAction CreateAction() => new ReverseTextAction();

    protected override ContextSnapshot CreateValidSnapshot()
    {
        return new ContextSnapshot(
            Guid.NewGuid(),
            new SourceWindowIdentity(IntPtr.Zero, 1, "notepad.exe", DateTimeOffset.UtcNow),
            ContextStatus.Available,
            new TextSelection("Some valid text to reverse"),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(5),
            "sample.provider",
            null
        );
    }
}

public class SampleActionsTests
{
    [Fact]
    public async Task CountWordsAction_Matches_ValidTextSelection()
    {
        var action = new CountWordsAction();
        var snapshot = CreateTextSnapshot("One two three");

        var availability = await action.MatchAsync(snapshot, CancellationToken.None);

        Assert.True(availability.IsAvailable);
    }

    [Fact]
    public async Task CountWordsAction_ExecuteAsync_CountsCorrectly()
    {
        var action = new CountWordsAction();
        var snapshot = CreateTextSnapshot("Hello world from unit test");

        var receipt = await action.ExecuteAsync(snapshot, new EmptyParameters(), null, CancellationToken.None);

        Assert.True(receipt.IsSuccess);
        Assert.Contains("5 words", receipt.Message);
    }

    [Fact]
    public async Task ReverseTextAction_Matches_ValidTextSelection()
    {
        var action = new ReverseTextAction();
        var snapshot = CreateTextSnapshot("To reverse");

        var availability = await action.MatchAsync(snapshot, CancellationToken.None);

        Assert.True(availability.IsAvailable);
    }

    [Fact]
    public async Task ReverseTextAction_PreviewAsync_ShowsReversedText()
    {
        var action = new ReverseTextAction();
        var snapshot = CreateTextSnapshot("Hello");

        var preview = await action.PreviewAsync(snapshot, new EmptyParameters(), CancellationToken.None);

        Assert.Contains("olleH", preview.PreviewText);
    }

    [Fact]
    public async Task ReverseTextAction_ExecuteAsync_Succeeds()
    {
        var action = new ReverseTextAction();
        var snapshot = CreateTextSnapshot("Test string");

        var receipt = await action.ExecuteAsync(snapshot, new EmptyParameters(), null, CancellationToken.None);

        Assert.True(receipt.IsSuccess);
        Assert.Single(receipt.OutputPaths);
    }

    private static ContextSnapshot CreateTextSnapshot(string text)
    {
        return new ContextSnapshot(
            Guid.NewGuid(),
            new SourceWindowIdentity(IntPtr.Zero, 1, "notepad.exe", DateTimeOffset.UtcNow),
            ContextStatus.Available,
            new TextSelection(text),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(5),
            "sample.provider",
            null
        );
    }
}