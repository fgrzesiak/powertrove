using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ContextBar.Abstractions;
using ContextBar.Pack.Tables;
using ContextBar.Abstractions.Files;
using Xunit;

namespace ContextBar.Pack.Tables.Tests;

public class ExportCsvActionTests
{
    private class InMemoryOutputFileSystem : IOutputFileSystem
    {
        public long GetFileLength(string path) { using var s = OpenRead(path); return s.Length; }
        public void MoveFile(string sourcePath, string destinationPath) => throw new System.NotSupportedException();
        public void MoveDirectory(string sourcePath, string destinationPath) => throw new System.NotSupportedException();
        public void DeleteDirectory(string path) => throw new System.NotSupportedException();

        public Dictionary<string, byte[]> Files = new();

        public bool FileExists(string path) => Files.ContainsKey(path);
        public bool DirectoryExists(string path) => string.IsNullOrEmpty(Path.GetExtension(path)); // Paths without an extension are folders
        public void CreateDirectory(string path) { }
        public void DeleteFile(string path) => Files.Remove(path);
        public Stream OpenRead(string path) => new MemoryStream(Files[path]);

        public Stream CreateFile(string path)
        {
            var stream = new MemoryStream();
            // Wrap in a custom stream so we can capture on close/dispose
            return new TrackedMemoryStream(stream, bytes => Files[path] = bytes);
        }

        private class TrackedMemoryStream : MemoryStream
        {
            private readonly MemoryStream _inner;
            private readonly Action<byte[]> _onClose;

            public TrackedMemoryStream(MemoryStream inner, Action<byte[]> onClose)
            {
                _inner = inner;
                _onClose = onClose;
            }

            public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
            public override void WriteByte(byte value) => _inner.WriteByte(value);
            public override void Flush() => _inner.Flush();
            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _onClose(_inner.ToArray());
                }
                base.Dispose(disposing);
                _inner.Dispose();
            }
        }
    }

    private ExportCsvAction CreateAction(out InMemoryOutputFileSystem fileSystem, out IOutputPlanner planner)
    {
        fileSystem = new InMemoryOutputFileSystem();
        planner = new OutputPlanner(fileSystem);
        return new ExportCsvAction(planner, fileSystem);
    }

    private ContextSnapshot CreateTableContext(IReadOnlyList<IReadOnlyList<CellValue>> rows)
    {
        return new ContextSnapshot(
            InvocationId: Guid.NewGuid(),
            Source: new SourceWindowIdentity(IntPtr.Zero, 1, "test", DateTimeOffset.UtcNow),
            Status: ContextStatus.Available,
            Content: new TableData(rows, "TestApp", "A1:B2"),
            CapturedAt: DateTimeOffset.UtcNow,
            ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(5),
            ProviderId: "TestProvider",
            UserMessage: null
        );
    }

    private sealed class CollisionFileSystem : IOutputFileSystem
    {
        public long GetFileLength(string path) { using var s = OpenRead(path); return s.Length; }
        public void MoveFile(string sourcePath, string destinationPath) => throw new System.NotSupportedException();
        public void MoveDirectory(string sourcePath, string destinationPath) => throw new System.NotSupportedException();
        public void DeleteDirectory(string path) => throw new System.NotSupportedException();

        // The file appears between PlanOutput and CreateFile.
        public bool DeleteCalled { get; private set; }
        public byte[] OriginalContent { get; } = Encoding.UTF8.GetBytes("DO NOT DELETE");
        public bool FileExists(string path) => false; // stale existence probe
        public bool DirectoryExists(string path) => string.IsNullOrEmpty(Path.GetExtension(path));
        public void CreateDirectory(string path) { }
        public void DeleteFile(string path) => DeleteCalled = true;
        public Stream OpenRead(string path) => new MemoryStream(OriginalContent);
        public Stream CreateFile(string path) => throw new IOException("Output file already exists.");
    }

    [Fact]
    public async Task ExecuteAsync_LateCollision_DoesNotDeleteExistingFile()
    {
        var fs = new CollisionFileSystem();
        var action = new ExportCsvAction(new OutputPlanner(fs), fs);
        var context = CreateTableContext(new CellValue[][] { new CellValue[] { new TextCell("test") } });
        var parameters = new DictionaryParameters(new Dictionary<string, string>
        {
            { "OutputDirectory", "/test" }, { "BaseFileName", "collision.csv" }
        });

        var receipt = await action.ExecuteAsync(context, parameters, null, CancellationToken.None);

        Assert.False(receipt.IsSuccess);
        Assert.False(fs.DeleteCalled);
        Assert.Equal("DO NOT DELETE", Encoding.UTF8.GetString(fs.OriginalContent));
    }

    [Fact]
    public async Task MatchAsync_ReturnsAvailableForTableData()
    {
        var action = CreateAction(out _, out _);
        var rows = new[] { new[] { new TextCell("A") } };
        var context = CreateTableContext(rows);

        var availability = await action.MatchAsync(context, CancellationToken.None);

        Assert.True(availability.IsAvailable);
    }

    [Fact]
    public async Task PreviewAsync_FormatsFirstRows()
    {
        var action = CreateAction(out _, out _);
        var rows = new[]
        {
            new[] { new TextCell("Col1"), new TextCell("Col2") },
            new[] { new TextCell("Val1"), new TextCell("Val2") }
        };
        var context = CreateTableContext(rows);

        var preview = await action.PreviewAsync(context, new EmptyParameters(), CancellationToken.None);

        Assert.Contains("Col1,Col2", preview.PreviewText);
        Assert.Contains("Val1,Val2", preview.PreviewText);
        Assert.True(preview.RequiresConfirmation);
    }

    [Fact]
    public async Task ExecuteAsync_RoundTripsQuotesAndNewlines()
    {
        var action = CreateAction(out var fs, out _);
        var rows = new CellValue[][]
        {
            new CellValue[] { new TextCell("Normal"), new TextCell("Has \"Quotes\"") },
            new CellValue[] { new TextCell("Has,Comma"), new TextCell("Has\r\nNewline") }
        };
        var context = CreateTableContext(rows);
        var parameters = new DictionaryParameters(new Dictionary<string, string>
        {
            { "OutputDirectory", "/test" },
            { "BaseFileName", "test.csv" }
        });

        var receipt = await action.ExecuteAsync(context, parameters, null, CancellationToken.None);

        Assert.True(receipt.IsSuccess);
        Assert.Single(receipt.OutputPaths);
        var outputPath = receipt.OutputPaths[0];

        Assert.True(fs.FileExists(outputPath));
        var content = Encoding.UTF8.GetString(fs.Files[outputPath]);

        var expected = "Normal,\"Has \"\"Quotes\"\"\"\r\n\"Has,Comma\",\"Has\r\nNewline\"\r\n";
        Assert.Equal(expected, content);
    }

    [Fact]
    public async Task ExecuteAsync_PreservesEmptyCells()
    {
        var action = CreateAction(out var fs, out _);
        var rows = new CellValue[][]
        {
            new CellValue[] { new TextCell("A"), new EmptyCell(), new TextCell("C") },
            new CellValue[] { new EmptyCell(), new TextCell("B"), new EmptyCell() }
        };
        var context = CreateTableContext(rows);
        var parameters = new DictionaryParameters(new Dictionary<string, string>
        {
            { "OutputDirectory", "/test" },
            { "BaseFileName", "test.csv" }
        });

        var receipt = await action.ExecuteAsync(context, parameters, null, CancellationToken.None);

        var outputPath = receipt.OutputPaths[0];
        var content = Encoding.UTF8.GetString(fs.Files[outputPath]);

        var expected = "A,,C\r\n,B,\r\n";
        Assert.Equal(expected, content);
    }

    [Fact]
    public async Task ExecuteAsync_FormatsDatesAndNumbersInvariant()
    {
        var action = CreateAction(out var fs, out _);
        var date = new DateTime(2023, 10, 27, 15, 30, 0);
        var rows = new CellValue[][]
        {
            new CellValue[] { new NumberCell(1234.56), new DateCell(date) }
        };
        var context = CreateTableContext(rows);
        var parameters = new DictionaryParameters(new Dictionary<string, string>
        {
            { "OutputDirectory", "/test" },
            { "BaseFileName", "test.csv" }
        });

        var receipt = await action.ExecuteAsync(context, parameters, null, CancellationToken.None);

        var outputPath = receipt.OutputPaths[0];
        var content = Encoding.UTF8.GetString(fs.Files[outputPath]);

        // "s" format is yyyy-MM-ddTHH:mm:ss
        var expected = "1234.56,2023-10-27T15:30:00\r\n";
        Assert.Equal(expected, content);
    }

    [Fact]
    public async Task ExecuteAsync_CreatesNewFileWithoutAlteringSource()
    {
        var action = CreateAction(out var fs, out var planner);
        var rows = new CellValue[][] { new CellValue[] { new TextCell("Test") } };
        var context = CreateTableContext(rows);

        // Seed the file system so "test.csv" already exists
        fs.Files["/test/test.csv"] = Encoding.UTF8.GetBytes("Original Content");

        var parameters = new DictionaryParameters(new Dictionary<string, string>
        {
            { "OutputDirectory", "/test" },
            { "BaseFileName", "test.csv" }
        });

        var receipt = await action.ExecuteAsync(context, parameters, null, CancellationToken.None);

        Assert.True(receipt.IsSuccess);
        var outputPath = receipt.OutputPaths[0];

        // Ensure a new file was created and the original wasn't overwritten
        Assert.NotEqual("/test/test.csv", outputPath);
        Assert.Equal("Original Content", Encoding.UTF8.GetString(fs.Files["/test/test.csv"]));

        var content = Encoding.UTF8.GetString(fs.Files[outputPath]);
        Assert.Equal("Test\r\n", content);
    }
}
