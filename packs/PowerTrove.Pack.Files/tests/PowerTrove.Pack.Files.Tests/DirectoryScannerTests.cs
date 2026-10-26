using System;
using System.IO;
using PowerTrove.Pack.Files;
using Xunit;

namespace PowerTrove.Pack.Files.Tests;

public class DirectoryScannerTests : IDisposable
{
    private readonly string _testRoot;
    private readonly DirectoryScanner _scanner;

    public DirectoryScannerTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "PowerTrove_DirScannerTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
        _scanner = new DirectoryScanner();
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, true);
        }
    }

    [Fact]
    public void EnumerateFiles_RespectsMaxDepth()
    {
        // Arrange
        // depth 0
        File.WriteAllText(Path.Combine(_testRoot, "file1.txt"), "test");
        var dir1 = Directory.CreateDirectory(Path.Combine(_testRoot, "dir1"));

        // depth 1
        File.WriteAllText(Path.Combine(dir1.FullName, "file2.txt"), "test");
        var dir2 = Directory.CreateDirectory(Path.Combine(dir1.FullName, "dir2"));

        // depth 2
        File.WriteAllText(Path.Combine(dir2.FullName, "file3.txt"), "test");

        // Act - Max depth 0 (only root files)
        var result0 = _scanner.EnumerateFiles(_testRoot, maxDepth: 0, maxItems: 100);
        Assert.Single(result0.Paths);
        Assert.Contains(result0.Paths, p => p.EndsWith("file1.txt"));

        // Act - Max depth 1 (root + dir1)
        var result1 = _scanner.EnumerateFiles(_testRoot, maxDepth: 1, maxItems: 100);
        Assert.Equal(2, result1.Paths.Count);
        Assert.Contains(result1.Paths, p => p.EndsWith("file1.txt"));
        Assert.Contains(result1.Paths, p => p.EndsWith("file2.txt"));
    }

    [Fact]
    public void EnumerateFiles_RespectsMaxItems()
    {
        // Arrange
        for (int i = 0; i < 5; i++)
        {
            File.WriteAllText(Path.Combine(_testRoot, $"file{i}.txt"), "test");
        }

        // Act
        var result = _scanner.EnumerateFiles(_testRoot, maxDepth: 1, maxItems: 3);

        // Assert
        Assert.True(result.LimitReached);
        Assert.Equal(3, result.Paths.Count);
        Assert.True(result.TotalItemsFound >= 3);
    }
}
