using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using PowerTrove.Abstractions.Files;

namespace PowerTrove.Core.Tests;

public class UndoServiceTests
{
    private class MockOutputFileSystem : IOutputFileSystem
    {
        public long GetFileLength(string path) { using var s = OpenRead(path); return s.Length; }
        public void MoveFile(string sourcePath, string destinationPath) => throw new System.NotSupportedException();
        public void MoveDirectory(string sourcePath, string destinationPath) => throw new System.NotSupportedException();
        public void DeleteDirectory(string path) => throw new System.NotSupportedException();

        private readonly Dictionary<string, byte[]> _files = new();
        public List<string> DeletedPaths { get; } = new();
        public bool ThrowOnDelete { get; set; } = false;

        public void AddFile(string path, string content)
        {
            _files[path] = Encoding.UTF8.GetBytes(content);
        }

        public void ModifyFile(string path, string newContent)
        {
            if (_files.ContainsKey(path))
            {
                _files[path] = Encoding.UTF8.GetBytes(newContent);
            }
        }

        public bool FileExists(string path) => _files.ContainsKey(path);

        public bool DirectoryExists(string path) => false; // Not needed for these tests
        public void CreateDirectory(string path) { }

        public void DeleteFile(string path)
        {
            if (ThrowOnDelete)
            {
                throw new IOException("Simulated file lock");
            }
            if (_files.ContainsKey(path))
            {
                _files.Remove(path);
                DeletedPaths.Add(path);
            }
        }

        public Stream OpenRead(string path)
        {
            if (_files.TryGetValue(path, out var bytes))
            {
                return new MemoryStream(bytes);
            }
            throw new FileNotFoundException(path);
        }

        public Stream CreateFile(string path) => throw new NotImplementedException();
    }

    [Fact]
    public async Task RecordOperationAsync_CreatesReceiptWithHashes()
    {
        // Arrange
        var fileSystem = new MockOutputFileSystem();
        fileSystem.AddFile("C:/temp/file1.txt", "content1");
        fileSystem.AddFile("C:/temp/file2.txt", "content2");
        var service = new UndoService(fileSystem);

        var operationId = Guid.NewGuid();
        var actionId = "test.action";
        var outputPaths = new List<string> { "C:/temp/file1.txt", "C:/temp/file2.txt" };

        // Act
        var receipt = await service.RecordOperationAsync(operationId, actionId, outputPaths, CancellationToken.None);

        // Assert
        Assert.Equal(operationId, receipt.OperationId);
        Assert.Equal(actionId, receipt.ActionId);
        Assert.Equal(2, receipt.Outputs.Count);

        // Verify history contains it
        var history = service.GetHistory();
        Assert.Single(history);
        Assert.Equal(receipt, history[0]);
    }

    [Fact]
    public async Task CanUndoAsync_ReturnsTrue_WhenFilesExistAndHashesMatch()
    {
        // Arrange
        var fileSystem = new MockOutputFileSystem();
        fileSystem.AddFile("C:/temp/file1.txt", "content1");
        var service = new UndoService(fileSystem);

        var operationId = Guid.NewGuid();
        await service.RecordOperationAsync(operationId, "test.action", new[] { "C:/temp/file1.txt" }, CancellationToken.None);

        // Act
        bool canUndo = await service.CanUndoAsync(operationId, CancellationToken.None);

        // Assert
        Assert.True(canUndo);
    }

    [Fact]
    public async Task CanUndoAsync_ReturnsFalse_WhenFileModified()
    {
        // Arrange
        var fileSystem = new MockOutputFileSystem();
        fileSystem.AddFile("C:/temp/file1.txt", "content1");
        var service = new UndoService(fileSystem);

        var operationId = Guid.NewGuid();
        await service.RecordOperationAsync(operationId, "test.action", new[] { "C:/temp/file1.txt" }, CancellationToken.None);

        // Modify file after recording
        fileSystem.ModifyFile("C:/temp/file1.txt", "modified content");

        // Act
        bool canUndo = await service.CanUndoAsync(operationId, CancellationToken.None);

        // Assert
        Assert.False(canUndo);
    }

    [Fact]
    public async Task CanUndoAsync_ReturnsFalse_WhenFileDeleted()
    {
        // Arrange
        var fileSystem = new MockOutputFileSystem();
        fileSystem.AddFile("C:/temp/file1.txt", "content1");
        var service = new UndoService(fileSystem);

        var operationId = Guid.NewGuid();
        await service.RecordOperationAsync(operationId, "test.action", new[] { "C:/temp/file1.txt" }, CancellationToken.None);

        // Delete file after recording
        fileSystem.DeleteFile("C:/temp/file1.txt");

        // Act
        bool canUndo = await service.CanUndoAsync(operationId, CancellationToken.None);

        // Assert
        Assert.False(canUndo);
    }

    [Fact]
    public async Task UndoAsync_Successful_RemovesFilesAndReceipt()
    {
        // Arrange
        var fileSystem = new MockOutputFileSystem();
        fileSystem.AddFile("C:/temp/file1.txt", "content1");
        fileSystem.AddFile("C:/temp/file2.txt", "content2");
        var service = new UndoService(fileSystem);

        var operationId = Guid.NewGuid();
        await service.RecordOperationAsync(operationId, "test.action", new[] { "C:/temp/file1.txt", "C:/temp/file2.txt" }, CancellationToken.None);

        // Act
        var result = await service.UndoAsync(operationId, CancellationToken.None);

        // Assert
        Assert.True(result.Success);
        Assert.Empty(result.FailedPaths);

        Assert.False(fileSystem.FileExists("C:/temp/file1.txt"));
        Assert.False(fileSystem.FileExists("C:/temp/file2.txt"));
        Assert.Equal(2, fileSystem.DeletedPaths.Count);

        // History should be clear
        Assert.Empty(service.GetHistory());
    }

    [Fact]
    public async Task UndoAsync_PartialFailure_ReturnsFailedPathsAndKeepsReceipt()
    {
        // Arrange
        var fileSystem = new MockOutputFileSystem();
        fileSystem.AddFile("C:/temp/file1.txt", "content1");
        var service = new UndoService(fileSystem);

        var operationId = Guid.NewGuid();
        await service.RecordOperationAsync(operationId, "test.action", new[] { "C:/temp/file1.txt" }, CancellationToken.None);

        fileSystem.ThrowOnDelete = true; // Simulate file lock during delete

        // Act
        var result = await service.UndoAsync(operationId, CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.Single(result.FailedPaths);
        Assert.Equal("C:/temp/file1.txt", result.FailedPaths[0]);

        // History should still have the receipt since it didn't complete
        Assert.Single(service.GetHistory());
    }
}