using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;

namespace PowerTrove.Pack.Files.Tests;

public sealed class CombinationToolsTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    public void Dispose() => _dir.Dispose();
    private static FileActionServices Services() => FileActionServices.CreatePhysical(new ExtensionOnlyTypeVerifier());
    [Fact]
    public async Task AddZipCopiesOldAndNewEntriesResolvesCollisionsAndPreservesOriginal()
    {
        string source = _dir.File("source.zip");
        using (var zip = ZipFile.Open(source, ZipArchiveMode.Create))
            foreach (string name in new[] { "same.txt", "old.txt" }) { using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write("old"); }
        byte[] hash = SHA256.HashData(File.ReadAllBytes(source));
        string same = _dir.Write("same.txt", "new"), child = _dir.Write("folder/child.txt", "child");
        var result = await new AddToZipAction(Services()).ExecuteAsync(Snapshots.ForPaths(source, same, Path.GetDirectoryName(child)!), new EmptyParameters(), null, default);
        Assert.True(result.IsSuccess); Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(source)));
        using var output = ZipFile.OpenRead(Assert.Single(result.OutputPaths));
        Assert.NotNull(output.GetEntry("old.txt")); Assert.NotNull(output.GetEntry("same.txt")); Assert.NotNull(output.GetEntry("same (2).txt")); Assert.NotNull(output.GetEntry("folder/child.txt"));
    }
    [Fact]
    public async Task UnsafeZipPublishesNothingAndKeepsSource()
    {
        string source = _dir.File("unsafe.zip");
        using (var zip = ZipFile.Open(source, ZipArchiveMode.Create)) { using var writer = new StreamWriter(zip.CreateEntry("../escape.txt").Open()); writer.Write("x"); }
        var extra = _dir.Write("extra.txt", "x");
        var result = await new AddToZipAction(Services()).ExecuteAsync(Snapshots.ForPaths(source, extra), new EmptyParameters(), null, default);
        Assert.False(result.IsSuccess); Assert.Empty(result.OutputPaths); Assert.Empty(_dir.NewFiles(source, extra));
    }
    [Fact]
    public async Task CompareRequiresExactlyTwoTextFilesAndReceiptContainsCountsOnly()
    {
        string a = _dir.Write("secret-a.txt", "one\ntwo\n"), b = _dir.Write("secret-b.txt", "one\n<script>\n"), c = _dir.Write("third.txt", "three");
        var action = new CompareTextAction(Services());
        Assert.Equal(SelectionFit.Ideal, SelectionRequirements.Evaluate(action.Descriptor.Selection!, Snapshots.ForPaths(a, b)));
        Assert.Equal(SelectionFit.None, SelectionRequirements.Evaluate(action.Descriptor.Selection!, Snapshots.ForPaths(a, b, c)));
        Assert.False((await action.MatchAsync(Snapshots.ForPaths(a, b, c), default)).IsAvailable);
        var result = await action.ExecuteAsync(Snapshots.ForPaths(a, b), new EmptyParameters(), null, default);
        Assert.True(result.IsSuccess); Assert.Equal("Added: 1; removed: 1; unchanged: 1.", result.Message);
        Assert.Contains("&lt;script&gt;", File.ReadAllText(Assert.Single(result.OutputPaths))); Assert.DoesNotContain("secret", result.Message!);
    }
    [Fact]
    public async Task GermanSortCopiesIntoStableCollisionSafeFolders()
    {
        var old = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("de-DE");
            string a = _dir.Write("a.png", "image"), b = _dir.Write("b.pdf", "pdf"), c = _dir.Write("c.txt", "text");
            Directory.CreateDirectory(_dir.File("Sorted"));
            var before = new[] { a, b, c }.Select(p => SHA256.HashData(File.ReadAllBytes(p))).ToArray();
            var result = await new SortByTypeAction(Services()).ExecuteAsync(Snapshots.ForPaths(a, b, c), new EmptyParameters(), null, default);
            Assert.True(result.IsSuccess); string folder = Assert.Single(result.OutputPaths); Assert.EndsWith("Sorted (2)", folder);
            Assert.True(File.Exists(Path.Combine(folder, "Images", "a.png"))); Assert.True(File.Exists(Path.Combine(folder, "PDFs", "b.pdf"))); Assert.True(File.Exists(Path.Combine(folder, "Text", "c.txt")));
            int i = 0; foreach (var p in new[] { a, b, c }) Assert.Equal(before[i++], SHA256.HashData(File.ReadAllBytes(p)));
        }
        finally { CultureInfo.CurrentUICulture = old; }
    }
    [Fact]
    public async Task SortingSameFamilyDoesNotMatchOrExecute()
    {
        var action = new SortByTypeAction(Services()); var s = Snapshots.ForPaths(_dir.Write("a.txt", "a"), _dir.Write("b.txt", "b"));
        Assert.False((await action.MatchAsync(s, default)).IsAvailable);
        Assert.False((await action.ExecuteAsync(s, new EmptyParameters(), null, default)).IsSuccess);
    }
}
