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

public class ExportSqlInsertActionTests
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
            return new TrackedMemoryStream(stream, bytes => Files[path] = bytes);
        }

        private class TrackedMemoryStream : MemoryStream
        {
            private readonly Action<byte[]> _onClose;

            public TrackedMemoryStream(MemoryStream inner, Action<byte[]> onClose)
            {
                _onClose = onClose;
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _onClose(this.ToArray());
                }
                base.Dispose(disposing);
            }
        }
    }

    private ExportSqlInsertAction CreateAction(out InMemoryOutputFileSystem fileSystem, out IOutputPlanner planner)
    {
        fileSystem = new InMemoryOutputFileSystem();
        planner = new OutputPlanner(fileSystem);
        return new ExportSqlInsertAction(planner, fileSystem);
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
    public async Task ExecuteAsync_FormatsDialectIdentifiersCorrectly()
    {
        var action = CreateAction(out var fs, out _);
        var rows = new CellValue[][]
        {
            new CellValue[] { new TextCell("id") },
            new CellValue[] { new TextCell("val") }
        };
        var context = CreateTableContext(rows);

        var dialects = new[]
        {
            ("PostgreSQL", "INSERT INTO \"ExportedData\" (\"id\") VALUES ('val');"),
            ("MySQL", "INSERT INTO `ExportedData` (`id`) VALUES ('val');"),
            ("SQLServer", "INSERT INTO [ExportedData] ([id]) VALUES (N'val');"),
            ("SQLite", "INSERT INTO \"ExportedData\" (\"id\") VALUES ('val');")
        };

        foreach (var (dialect, expected) in dialects)
        {
            var parameters = new DictionaryParameters(new Dictionary<string, string>
            {
                { "Dialect", dialect },
                { "FirstRowAsHeader", "true" },
                { "TableName", "ExportedData" },
                { "BaseFileName", $"{dialect}.sql" },
                { "OutputDirectory", "/test" }
            });

            var receipt = await action.ExecuteAsync(context, parameters, null, CancellationToken.None);
            Assert.True(receipt.IsSuccess);

            var content = Encoding.UTF8.GetString(fs.Files[receipt.OutputPaths[0]]);
            Assert.Contains(expected, content);
        }
    }

    [Theory]
    [InlineData("PostgreSQL", "odd\"table", "odd\"column", "\"odd\"\"table\"", "\"odd\"\"column\"")]
    [InlineData("SQLite", "odd\"table", "odd\"column", "\"odd\"\"table\"", "\"odd\"\"column\"")]
    [InlineData("MySQL", "odd`table", "odd`column", "`odd``table`", "`odd``column`")]
    [InlineData("SQLServer", "odd]table", "odd]column", "[odd]]table]", "[odd]]column]")]
    public async Task ExecuteAsync_EscapesIdentifiersForEachDialect(
        string dialect, string tableName, string columnName, string expectedTable, string expectedColumn)
    {
        var action = CreateAction(out var fs, out _);
        var context = CreateTableContext(new CellValue[][]
        {
            new CellValue[] { new TextCell(columnName) },
            new CellValue[] { new TextCell("value") }
        });
        var parameters = new DictionaryParameters(new Dictionary<string, string>
        {
            { "Dialect", dialect },
            { "FirstRowAsHeader", "true" },
            { "TableName", tableName },
            { "OutputDirectory", "/test" }
        });

        var receipt = await action.ExecuteAsync(context, parameters, null, CancellationToken.None);
        Assert.True(receipt.IsSuccess);
        var sql = Encoding.UTF8.GetString(fs.Files[receipt.OutputPaths[0]]);
        Assert.Contains($"INSERT INTO {expectedTable} ({expectedColumn})", sql);
    }

    [Fact]
    public async Task ExecuteAsync_RejectsUnsupportedDialect()
    {
        var action = CreateAction(out var fs, out _);
        var context = CreateTableContext(new CellValue[][] { new CellValue[] { new TextCell("hello") } });
        var parameters = new DictionaryParameters(new Dictionary<string, string>
        {
            { "Dialect", "UnknownVendor" }, { "OutputDirectory", "/test" }
        });

        var receipt = await action.ExecuteAsync(context, parameters, null, CancellationToken.None);
        Assert.False(receipt.IsSuccess);
        Assert.Empty(fs.Files);
    }

    [Fact]
    public async Task ExecuteAsync_HandlesNullValues()
    {
        var action = CreateAction(out var fs, out _);
        var rows = new CellValue[][]
        {
            new CellValue[] { new EmptyCell(), new ErrorCell("#REF!") }
        };
        var context = CreateTableContext(rows);

        var parameters = new DictionaryParameters(new Dictionary<string, string>
        {
            { "Dialect", "PostgreSQL" },
            { "BaseFileName", "nulls.sql" },
            { "OutputDirectory", "/test" }
        });

        var receipt = await action.ExecuteAsync(context, parameters, null, CancellationToken.None);
        Assert.True(receipt.IsSuccess);

        var content = Encoding.UTF8.GetString(fs.Files[receipt.OutputPaths[0]]);
        Assert.Contains("VALUES (NULL, NULL);", content);
    }

    [Fact]
    public async Task ExecuteAsync_EscapesApostrophes()
    {
        var action = CreateAction(out var fs, out _);
        var rows = new CellValue[][]
        {
            new CellValue[] { new TextCell("O'Connor") }
        };
        var context = CreateTableContext(rows);

        var parameters = new DictionaryParameters(new Dictionary<string, string>
        {
            { "Dialect", "PostgreSQL" },
            { "BaseFileName", "quotes.sql" },
            { "OutputDirectory", "/test" }
        });

        var receipt = await action.ExecuteAsync(context, parameters, null, CancellationToken.None);
        Assert.True(receipt.IsSuccess);

        var content = Encoding.UTF8.GetString(fs.Files[receipt.OutputPaths[0]]);
        Assert.Contains("VALUES ('O''Connor');", content);
    }

    [Fact]
    public async Task ExecuteAsync_HandlesUnicodeAndDataTypes()
    {
        var action = CreateAction(out var fs, out _);
        var date = new DateTime(2023, 10, 27, 15, 30, 0);
        var rows = new CellValue[][]
        {
            new CellValue[] { new TextCell("こんにちは"), new NumberCell(42.5), new BooleanCell(true), new DateCell(date) }
        };
        var context = CreateTableContext(rows);

        var parameters = new DictionaryParameters(new Dictionary<string, string>
        {
            { "Dialect", "SQLServer" },
            { "BaseFileName", "unicode.sql" },
            { "OutputDirectory", "/test" }
        });

        var receipt = await action.ExecuteAsync(context, parameters, null, CancellationToken.None);
        Assert.True(receipt.IsSuccess);

        var content = Encoding.UTF8.GetString(fs.Files[receipt.OutputPaths[0]]);
        Assert.Contains("VALUES (N'こんにちは', 42.5, 1, N'2023-10-27T15:30:00');", content);
    }

    [Fact]
    public async Task PreviewAsync_ShowsUpToFiveRows()
    {
        var action = CreateAction(out var fs, out _);
        var rows = new CellValue[][]
        {
            new CellValue[] { new TextCell("A") },
            new CellValue[] { new TextCell("B") },
            new CellValue[] { new TextCell("C") },
            new CellValue[] { new TextCell("D") },
            new CellValue[] { new TextCell("E") },
            new CellValue[] { new TextCell("F") },
            new CellValue[] { new TextCell("G") }
        };
        var context = CreateTableContext(rows);

        var parameters = new DictionaryParameters(new Dictionary<string, string>
        {
            { "Dialect", "PostgreSQL" }
        });

        var preview = await action.PreviewAsync(context, parameters, CancellationToken.None);

        Assert.Contains("VALUES ('A');", preview.PreviewText);
        Assert.Contains("VALUES ('E');", preview.PreviewText);
        Assert.DoesNotContain("VALUES ('F');", preview.PreviewText);
        Assert.Contains("preview truncated", preview.PreviewText);
    }
}
