using ContextBar.Abstractions.Testing;
using ContextBar.Application.Packs;
using ContextBar.Core.Extensibility.PackProtocol;

namespace ContextBar.Application.Tests;

public sealed class PackOutputCommitterTests
{
    [Fact]
    public async Task DirectoryOutputsAreCopiedThroughTheTransactionalWriterAndRenamedOnCollision()
    {
        using var temp = new TempDirectory();
        string input = temp.Write("source.txt", "original");
        Directory.CreateDirectory(temp.File("folder"));
        temp.Write("stage/result/nested/file.txt", "generated");
        var message = new ResultMessage(Guid.NewGuid(), new ActionReceipt(true, false, ["result"], [], false),
            [new StagedOutput("result", temp.Path, "folder")]);
        var receipt = await new PackOutputCommitter().CommitAsync(temp.File("stage"), FileSelectionPayload.FromSnapshot(Snapshots.ForPaths(input)), message, default);
        Assert.True(receipt.IsSuccess);
        Assert.Equal(temp.File("folder (1)"), Assert.Single(receipt.OutputPaths));
        Assert.Equal("generated", File.ReadAllText(temp.File("folder (1)/nested/file.txt")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.File("folder")));
        Assert.Equal("original", File.ReadAllText(input));
    }

    [Fact]
    public async Task EntireInventoryIsValidatedBeforeCreatingAnyDestination()
    {
        using var temp = new TempDirectory();
        string input = temp.Write("source.txt", "original");
        temp.Write("stage/first.txt", "generated");
        var message = new ResultMessage(Guid.NewGuid(), new ActionReceipt(true, false, ["first.txt", "missing.txt"], [], false),
            [new StagedOutput("first.txt", temp.Path, "copy.txt"), new StagedOutput("missing.txt", temp.Path, "another.txt")]);
        var receipt = await new PackOutputCommitter().CommitAsync(temp.File("stage"), FileSelectionPayload.FromSnapshot(Snapshots.ForPaths(input)), message, default);
        Assert.False(receipt.IsSuccess);
        Assert.Empty(receipt.OutputPaths);
        Assert.False(File.Exists(temp.File("copy.txt")));
    }
}
