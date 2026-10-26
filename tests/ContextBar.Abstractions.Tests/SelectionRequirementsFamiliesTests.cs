using System;
using System.Collections.Generic;
using ContextBar.Abstractions;
using Xunit;

namespace ContextBar.Abstractions.Tests;

public class SelectionRequirementsFamiliesTests
{
    private static ContextSnapshot Files(params string[] names)
    {
        var files = new List<SelectedFile>();
        foreach (var name in names)
            files.Add(new SelectedFile(@"C:\x\" + name, name, System.IO.Path.GetExtension(name), null, 1, true, true, null));
        return new ContextSnapshot(Guid.NewGuid(), new SourceWindowIdentity(IntPtr.Zero, 1, "test", DateTimeOffset.UtcNow),
            ContextStatus.Available, new FileSelection(files), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), "test", null);
    }

    private static readonly SelectionRequirements Mixed = new(
        new HashSet<string>(new[] { "pdf", "image" }, StringComparer.OrdinalIgnoreCase),
        MinItems: 2, IdealMinItems: 2, IdealNeedsEveryFamily: true);

    [Fact]
    public void IdealNeedsEveryFamily_RequiresAllListedFamilies()
    {
        Assert.Equal(SelectionFit.Ideal, SelectionRequirements.Evaluate(Mixed, Files("a.pdf", "b.png")));
        Assert.Equal(SelectionFit.Acceptable, SelectionRequirements.Evaluate(Mixed, Files("a.png", "b.jpg")));
        Assert.Equal(SelectionFit.None, SelectionRequirements.Evaluate(Mixed, Files("a.png")));
    }

    [Fact]
    public void DefaultBehaviorIsUnchanged()
    {
        var any = Mixed with { IdealNeedsEveryFamily = false };

        Assert.Equal(SelectionFit.Ideal, SelectionRequirements.Evaluate(any, Files("a.png", "b.jpg")));
    }
}
