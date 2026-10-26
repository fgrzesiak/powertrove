using System;
using System.Collections.Generic;
using PowerTrove.Abstractions;
using Xunit;

namespace PowerTrove.Abstractions.Tests;

public class ActionContextCompatibilityTests
{
    private static ContextSnapshot Snapshot(ContextStatus status) => new(
        Guid.NewGuid(), new SourceWindowIdentity(IntPtr.Zero, 1, "test", DateTimeOffset.UtcNow),
        status, new NoContent(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), "test", null);

    private static ActionDescriptor Descriptor(params ContentKind[] accepts) => new(
        "test.action", "Test", "Test", new HashSet<ContentKind>(accepts), Array.Empty<string>(), false, false);

    [Theory]
    [InlineData(ContextStatus.Failed)]
    [InlineData(ContextStatus.Unsupported)]
    [InlineData(ContextStatus.Empty)]
    [InlineData(ContextStatus.Available)]
    public void ContextFreeActions_AreAlwaysAvailable(ContextStatus status)
    {
        Assert.True(ActionContextCompatibility.Check(Snapshot(status), Descriptor(ContentKind.None)).IsAvailable);
    }

    [Theory]
    [InlineData(ContextStatus.Failed)]
    [InlineData(ContextStatus.Unsupported)]
    public void SelectionActions_StayUnavailableWithoutContext(ContextStatus status)
    {
        Assert.False(ActionContextCompatibility.Check(Snapshot(status), Descriptor(ContentKind.Files)).IsAvailable);
    }
}
