using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ContextBar.Pack.Tables;
using ContextBar.Abstractions.Files;
using Xunit;

namespace ContextBar.Pack.Tables.Tests;

public class ExportMarkdownActionTests
{
    private class FakePlanner : IOutputPlanner
    {
        public OutputPlan PlanFile(string targetDirectory, string baseName, string extension, System.Collections.Generic.ISet<string>? reserved = null) => PlanOutput(baseName + extension, targetDirectory, extension);
        public OutputPlan PlanDirectory(string targetDirectory, string name, System.Collections.Generic.ISet<string>? reserved = null) => throw new System.NotSupportedException();

        public OutputPlan PlanOutput(string originalName, string targetDirectory, string preferredExtension, bool overwrite = false)
        {
            return new OutputPlan(Path.Combine(targetDirectory, originalName), "test.md", false, overwrite);
        }

        public void PrepareOutputFolder(OutputPlan plan)
        {
        }
    }

    private class FakeFileSystem : IOutputFileSystem
    {
        public long GetFileLength(string path) { using var s = OpenRead(path); return s.Length; }
        public void MoveFile(string sourcePath, string destinationPath) => throw new System.NotSupportedException();
        public void MoveDirectory(string sourcePath, string destinationPath) => throw new System.NotSupportedException();
        public void DeleteDirectory(string path) => throw new System.NotSupportedException();

        public MemoryStream CapturedStream { get; } = new MemoryStream();

        public bool FileExists(string path) => false;
        public bool DirectoryExists(string path) => false;
        public void CreateDirectory(string path) { }
        public Stream OpenRead(string path) => throw new NotImplementedException();

        public Stream CreateFile(string path)
        {
            return CapturedStream;
        }

        public void DeleteFile(string path)
        {
        }
    }

    private readonly FakePlanner _planner = new();
    private readonly FakeFileSystem _fileSystem = new();
    private readonly ExportMarkdownAction _action;

    public ExportMarkdownActionTests()
    {
        _action = new ExportMarkdownAction(_planner, _fileSystem);
    }

    [Fact]
    public async Task MatchAsync_WithValidTable_ReturnsAvailable()
    {
        var rows = new List<IReadOnlyList<CellValue>>
        {
            new List<CellValue> { new TextCell("A1") }
        };
        var context = new ContextSnapshot(
            Guid.NewGuid(),
            new SourceWindowIdentity(IntPtr.Zero, 1, "test", DateTimeOffset.Now),
            ContextStatus.Available,
            new TableData(rows, null, null),
            DateTimeOffset.Now,
            DateTimeOffset.Now.AddMinutes(1),
            "provider",
            null);

        var result = await _action.MatchAsync(context, CancellationToken.None);

        Assert.True(result.IsAvailable);
    }

    [Fact]
    public async Task MatchAsync_WithEmptyTable_ReturnsUnavailable()
    {
        var rows = new List<IReadOnlyList<CellValue>>();
        var context = new ContextSnapshot(
            Guid.NewGuid(),
            new SourceWindowIdentity(IntPtr.Zero, 1, "test", DateTimeOffset.Now),
            ContextStatus.Available,
            new TableData(rows, null, null),
            DateTimeOffset.Now,
            DateTimeOffset.Now.AddMinutes(1),
            "provider",
            null);

        var result = await _action.MatchAsync(context, CancellationToken.None);

        Assert.False(result.IsAvailable);
        Assert.Equal("Table is empty.", result.Reason);
    }

    [Fact]
    public async Task PreviewAsync_FormatsNormalCellsCorrectly()
    {
        var rows = new List<IReadOnlyList<CellValue>>
        {
            new List<CellValue> { new TextCell("Col1"), new TextCell("Col2") },
            new List<CellValue> { new TextCell("Val1"), new NumberCell(42) }
        };
        var context = new ContextSnapshot(
            Guid.NewGuid(),
            new SourceWindowIdentity(IntPtr.Zero, 1, "test", DateTimeOffset.Now),
            ContextStatus.Available,
            new TableData(rows, null, null),
            DateTimeOffset.Now,
            DateTimeOffset.Now.AddMinutes(1),
            "provider",
            null);

        var preview = await _action.PreviewAsync(context, new DictionaryParameters(new Dictionary<string, string>()), CancellationToken.None);

        Assert.True(preview.RequiresConfirmation);
        var expectedLines = new[]
        {
            "| Col1 | Col2 |",
            "| --- | --- |",
            "| Val1 | 42 |"
        };
        var actualLines = preview.PreviewText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(expectedLines, actualLines);
    }

    [Fact]
    public async Task ExecuteAsync_EscapesPipesAndNewlines()
    {
        var rows = new List<IReadOnlyList<CellValue>>
        {
            new List<CellValue> { new TextCell("Header1"), new TextCell("Header2|Pipe") },
            new List<CellValue> { new TextCell("Line1\nLine2"), new TextCell("Normal") }
        };
        var context = new ContextSnapshot(
            Guid.NewGuid(),
            new SourceWindowIdentity(IntPtr.Zero, 1, "test", DateTimeOffset.Now),
            ContextStatus.Available,
            new TableData(rows, null, null),
            DateTimeOffset.Now,
            DateTimeOffset.Now.AddMinutes(1),
            "provider",
            null);

        var receipt = await _action.ExecuteAsync(context, new DictionaryParameters(new Dictionary<string, string>()), null, CancellationToken.None);

        Assert.True(receipt.IsSuccess);

        var output = Encoding.UTF8.GetString(_fileSystem.CapturedStream.ToArray());
        var lines = output.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("| Header1 | Header2&#124;Pipe |", lines[0]);
        Assert.Equal("| --- | --- |", lines[1]);
        Assert.Equal("| Line1<br>Line2 | Normal |", lines[2]);
    }
}
