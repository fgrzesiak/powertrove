using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PowerTrove.Abstractions;
using PowerTrove.Pack.Tables;
using PowerTrove.Abstractions.Files;
using Xunit;

namespace PowerTrove.Pack.Tables.Tests;

public class ExportJsonActionTests
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

    private ExportJsonAction CreateAction(out InMemoryOutputFileSystem fileSystem, out IOutputPlanner planner)
    {
        fileSystem = new InMemoryOutputFileSystem();
        planner = new OutputPlanner(fileSystem);
        return new ExportJsonAction(planner, fileSystem);
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
    public async Task PreviewAsync_FormatsFirstRows_AsArrays()
    {
        var action = CreateAction(out _, out _);
        var rows = new[]
        {
            new[] { new TextCell("Col1"), new TextCell("Col2") },
            new[] { new TextCell("Val1"), new TextCell("Val2") }
        };
        var context = CreateTableContext(rows);
        var parameters = new DictionaryParameters(new Dictionary<string, string>
        {
            { "FirstRowAsHeader", "false" }
        });

        var preview = await action.PreviewAsync(context, parameters, CancellationToken.None);

        Assert.Contains("[\n  [\n    \"Col1\",\n    \"Col2\"\n  ],\n  [\n    \"Val1\",\n    \"Val2\"\n  ]\n]", preview.PreviewText.Replace("\r\n", "\n"));
        Assert.True(preview.RequiresConfirmation);
    }

    [Fact]
    public async Task PreviewAsync_FormatsFirstRows_AsObjects()
    {
        var action = CreateAction(out _, out _);
        var rows = new[]
        {
            new[] { new TextCell("Col1"), new TextCell("Col2") },
            new[] { new TextCell("Val1"), new TextCell("Val2") }
        };
        var context = CreateTableContext(rows);
        var parameters = new DictionaryParameters(new Dictionary<string, string>
        {
            { "FirstRowAsHeader", "true" }
        });

        var preview = await action.PreviewAsync(context, parameters, CancellationToken.None);

        Assert.Contains("[\n  {\n    \"Col1\": \"Val1\",\n    \"Col2\": \"Val2\"\n  }\n]", preview.PreviewText.Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task ExecuteAsync_ExportsArrayOfArrays_WhenNoHeaderPolicy()
    {
        var action = CreateAction(out var fs, out _);
        var rows = new CellValue[][]
        {
            new CellValue[] { new TextCell("Col1"), new TextCell("Col2") },
            new CellValue[] { new TextCell("Val1"), new TextCell("Val2") }
        };
        var context = CreateTableContext(rows);
        var parameters = new DictionaryParameters(new Dictionary<string, string>
        {
            { "OutputDirectory", "/test" },
            { "BaseFileName", "test.json" },
            { "FirstRowAsHeader", "false" }
        });

        var receipt = await action.ExecuteAsync(context, parameters, null, CancellationToken.None);

        Assert.True(receipt.IsSuccess);
        var outputPath = receipt.OutputPaths[0];
        var content = Encoding.UTF8.GetString(fs.Files[outputPath]);

        Assert.Contains("[\n  [\n    \"Col1\",\n    \"Col2\"\n  ],\n  [\n    \"Val1\",\n    \"Val2\"\n  ]\n]", content.Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task ExecuteAsync_ExportsArrayOfObjects_ResolvingDuplicateHeaders()
    {
        var action = CreateAction(out var fs, out _);
        var rows = new CellValue[][]
        {
            new CellValue[] { new TextCell("Dupe"), new TextCell("Dupe"), new EmptyCell() },
            new CellValue[] { new TextCell("V1"), new TextCell("V2"), new TextCell("V3") }
        };
        var context = CreateTableContext(rows);
        var parameters = new DictionaryParameters(new Dictionary<string, string>
        {
            { "OutputDirectory", "/test" },
            { "BaseFileName", "test.json" },
            { "FirstRowAsHeader", "true" }
        });

        var receipt = await action.ExecuteAsync(context, parameters, null, CancellationToken.None);

        Assert.True(receipt.IsSuccess);
        var outputPath = receipt.OutputPaths[0];
        var content = Encoding.UTF8.GetString(fs.Files[outputPath]);

        Assert.Contains("\"Dupe\": \"V1\"", content);
        Assert.Contains("\"Dupe_1\": \"V2\"", content);
        Assert.Contains("\"Column_3\": \"V3\"", content);
    }

    [Fact]
    public async Task ExecuteAsync_FormatsDataTypesProperly()
    {
        var action = CreateAction(out var fs, out _);
        var date = new DateTime(2023, 10, 27, 15, 30, 0);
        var rows = new CellValue[][]
        {
            new CellValue[] { new NumberCell(1234.56), new BooleanCell(true), new EmptyCell(), new ErrorCell("#DIV/0!"), new DateCell(date) }
        };
        var context = CreateTableContext(rows);
        var parameters = new DictionaryParameters(new Dictionary<string, string>
        {
            { "OutputDirectory", "/test" },
            { "BaseFileName", "test.json" },
            { "FirstRowAsHeader", "false" }
        });

        var receipt = await action.ExecuteAsync(context, parameters, null, CancellationToken.None);

        var outputPath = receipt.OutputPaths[0];
        var content = Encoding.UTF8.GetString(fs.Files[outputPath]);

        Assert.Contains("1234.56", content);
        Assert.Contains("true", content);
        Assert.Contains("null", content);
        Assert.Contains("\"#DIV/0!\"", content);
        Assert.Contains("\"2023-10-27T15:30:00\"", content);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsFailed_WhenContextIsStale()
    {
        var action = CreateAction(out var fs, out _);
        var rows = new CellValue[][] { new CellValue[] { new TextCell("Test") } };

        var context = new ContextSnapshot(
            InvocationId: Guid.NewGuid(),
            Source: new SourceWindowIdentity(IntPtr.Zero, 1, "test", DateTimeOffset.UtcNow),
            Status: ContextStatus.Available,
            Content: new TableData(rows, "TestApp", "A1:B2"),
            CapturedAt: DateTimeOffset.UtcNow.AddMinutes(-10),
            ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(-5), // Stale
            ProviderId: "TestProvider",
            UserMessage: null
        );

        var receipt = await action.ExecuteAsync(context, new DictionaryParameters(new Dictionary<string, string>()), null, CancellationToken.None);

        Assert.False(receipt.IsSuccess);
        Assert.Empty(fs.Files);
        Assert.Contains(receipt.Failures, f => f.ErrorMessage.Contains("expired"));
    }
}
