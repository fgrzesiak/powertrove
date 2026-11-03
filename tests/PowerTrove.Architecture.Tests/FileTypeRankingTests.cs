using PowerTrove.Abstractions.Files;
using PowerTrove.Abstractions.Testing;
using PowerTrove.Core;
using PowerTrove.Core.Search;
using PowerTrove.Pack.Data;
using PowerTrove.Pack.Files;
using PowerTrove.Pack.Markdown;

namespace PowerTrove.Architecture.Tests;

/// <summary>
/// Browsing without a query: last used for this kind of selection, then actions made for the file type,
/// then general file actions. Reported on 2026-11-01: "Compress to ZIP" led for a single Markdown file.
/// </summary>
public sealed class FileTypeRankingTests
{
    private static ActionRegistry Registry()
    {
        var host = PackFactoriesTests.Host();
        var registry = new ActionRegistry();
        foreach (var pack in new IActionPack[] { new FilesPackFactory().Create(host), new DataPackFactory().Create(host), new MarkdownPackFactory().Create(host) })
            foreach (var action in pack.Actions) registry.Register(action);
        return registry;
    }

    private static ContextSnapshot Markdown() => Snapshots.ForPaths(Path.Combine(Path.GetTempPath(), "notes.md"));

    [Fact]
    public async Task FileTypeActionsLeadGeneralFileActionsEvenWhenTheseWereUsedRecently()
    {
        // Recently used everywhere, but never for Markdown: global recency must not cross the tiers.
        var recent = new Dictionary<string, int> { ["powertrove.files.zip"] = 40, ["powertrove.files.rename"] = 40 };
        var results = (await new ActionSearchEngine(Registry(), recentUsageScores: recent).SearchAsync("", Markdown())).Where(r => r.IsCompatible).ToList();

        Assert.Equal(MarkdownViewAction.ViewId, results[0].Action.Descriptor.Id);
        Assert.Equal(ActionHighlight.BestFit, results[0].Highlight);
        int lastSpecific = results.FindLastIndex(r => r.Action.Descriptor.Id.StartsWith("powertrove.markdown.", StringComparison.Ordinal)
            || r.Action.Descriptor.Id.StartsWith("powertrove.data.", StringComparison.Ordinal));
        int firstGeneral = results.FindIndex(r => r.Action.Descriptor.Selection is not { Families.Count: > 0 }
            && r.Action is not IFileActionMetadata { InputExtensions.Count: > 0 });
        Assert.True(firstGeneral > 0);
        Assert.Contains(results.Skip(firstGeneral), r => r.Action.Descriptor.Id == "powertrove.files.zip");
        Assert.True(firstGeneral > lastSpecific, string.Join(", ", results.Select(r => r.Action.Descriptor.Id)));
        // Markdown actions (5 formats) come before the general text conversions (16 formats).
        Assert.True(results.FindLastIndex(r => r.Action.Descriptor.Id.StartsWith("powertrove.markdown.", StringComparison.Ordinal))
            < results.FindIndex(r => r.Action.Descriptor.Id.StartsWith("powertrove.data.", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task SeveralMarkdownFilesLeadWithMergingThem()
    {
        var two = Snapshots.ForPaths(Path.Combine(Path.GetTempPath(), "a.md"), Path.Combine(Path.GetTempPath(), "b.md"));
        var results = (await new ActionSearchEngine(Registry()).SearchAsync("", two)).Where(r => r.IsCompatible).ToList();
        Assert.Equal(MergeMarkdownAction.Id, results[0].Action.Descriptor.Id);
        Assert.Equal(ActionHighlight.BestFit, results[0].Highlight);
        Assert.Contains(results.Take(4), r => r.Action.Descriptor.Id == "powertrove.markdown.to-pdf");
    }

    [Fact]
    public async Task TheActionLastUsedForThisFileTypeLeadsEverything()
    {
        var results = (await new ActionSearchEngine(Registry(), new LastUsed("powertrove.files.zip")).SearchAsync("", Markdown())).Where(r => r.IsCompatible).ToList();
        Assert.Equal("powertrove.files.zip", results[0].Action.Descriptor.Id);
        Assert.Equal(ActionHighlight.LastUsedForSelection, results[0].Highlight);
        Assert.Equal(MarkdownViewAction.ViewId, results[1].Action.Descriptor.Id);
    }

    private sealed class LastUsed(string id) : IActionUsageSignals
    {
        public double SelectionScore(string signatureKey, string actionId) => actionId == id ? 3 : 0;
        public string? LastUsedAction(string signatureKey) => id;
        public void Refresh() { }
    }
}
