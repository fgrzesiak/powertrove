using PowerTrove.Core.Extensibility.PackProtocol;

namespace PowerTrove.PackHost.Tests;

public sealed class StagingOutputFileSystemTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task PathBasedAndStreamEnginesWriteOnlyToStagingWhilePlanningAgainstRealCollisions()
    {
        string input = _temp.Write("source.txt", "original");
        _temp.Write("copy.txt", "existing");
        string staging = Directory.CreateDirectory(_temp.File("stage")).FullName;
        var fs = Create(input, staging);
        var writer = new FileOutputWriter(fs, new OutputPlanner(fs));
        string? enginePath = null;
        string output = await writer.WriteFileFromPathAsync(Path.GetDirectoryName(input)!, "copy", ".txt", async (path, token) =>
        { enginePath = path; await File.WriteAllTextAsync(path, "new content", token); }, default);
        Assert.StartsWith(staging + Path.DirectorySeparatorChar, enginePath);
        Assert.Equal(_temp.File("copy (1).txt"), output);
        Assert.False(File.Exists(output));
        Assert.Equal("original", File.ReadAllText(input));
        Assert.Equal("existing", File.ReadAllText(_temp.File("copy.txt")));
        var staged = Assert.Single(fs.DescribeOutputs(writer.Outputs));
        Assert.Equal("copy (1).txt", staged.IntendedName);
        Assert.Equal("new content", File.ReadAllText(Path.Combine(staging, staged.Path)));
        await writer.WriteFileAsync(Path.GetDirectoryName(input)!, "stream", ".txt",
            (stream, token) => stream.WriteAsync("stream content"u8.ToArray(), token).AsTask(), default);
        Assert.False(File.Exists(_temp.File("stream.txt")));
        Assert.Equal(2, fs.DescribeOutputs(writer.Outputs).Count);
    }

    [Fact]
    public void ReadsAreSelectionScopedAndInputsCannotBeDeletedOrMoved()
    {
        string input = _temp.Write("source.txt", "original");
        string other = _temp.Write("private.txt", "private");
        string stage = Directory.CreateDirectory(_temp.File("stage")).FullName;
        var fs = Create(input, stage);
        using (var read = fs.OpenRead(input)) Assert.True(read.CanRead);
        Assert.Throws<UnauthorizedAccessException>(() => fs.OpenRead(other));
        Assert.Throws<UnauthorizedAccessException>(() => fs.GetFileLength(other));
        Assert.Throws<UnauthorizedAccessException>(() => fs.DeleteFile(input));
        Assert.Throws<UnauthorizedAccessException>(() => fs.MoveFile(input, _temp.File("moved.txt")));
        Assert.Equal("original", File.ReadAllText(input));
    }

    [Fact]
    public async Task DirectoryEngineReceivesAStagingPathAndOnlyTheCommittedFolderIsDescribed()
    {
        string input = _temp.Write("source.txt", "original");
        string stage = Directory.CreateDirectory(_temp.File("stage")).FullName;
        var fs = Create(input, stage);
        var writer = new FileOutputWriter(fs, new OutputPlanner(fs));
        await writer.WriteDirectoryAsync(Path.GetDirectoryName(input)!, "folder", (path, token) =>
            File.WriteAllTextAsync(Path.Combine(path, "generated.txt"), "generated", token), default);
        Assert.False(Directory.Exists(_temp.File("folder")));
        var result = Assert.Single(fs.DescribeOutputs(writer.Outputs));
        Assert.True(File.Exists(Path.Combine(stage, result.Path, "generated.txt")));
    }

    [Fact]
    public void PreviewCannotCreateOrDeleteFiles()
    {
        string input = _temp.Write("source.txt", "original");
        var fs = new StagingOutputFileSystem(Selection(input), _temp.File("preview"), true);
        Assert.Throws<UnauthorizedAccessException>(() => fs.CreateFile(_temp.File("new.txt")));
        Assert.Throws<UnauthorizedAccessException>(() => fs.DeleteFile(input));
    }

    [Fact]
    public void SymlinkedSelectionAndStagingAreRejectedWithoutFollowingThem()
    {
        if (!OperatingSystem.IsWindows()) return;
        string input = _temp.Write("source.txt", "original");
        string target = Directory.CreateDirectory(_temp.File("target")).FullName;
        string link = _temp.File("link");
        try { Directory.CreateSymbolicLink(link, target); }
        catch (UnauthorizedAccessException) { return; } // Developer mode/elevation is an explicit prerequisite.
        Assert.Throws<UnauthorizedAccessException>(() => Create(input, link));
    }

    private static FileSelectionPayload Selection(string input) => new([new ProtocolFile(input, Path.GetFileName(input), ".txt", false, new FileInfo(input).Length)]);
    private static StagingOutputFileSystem Create(string input, string staging) => new(Selection(input), staging);
}
