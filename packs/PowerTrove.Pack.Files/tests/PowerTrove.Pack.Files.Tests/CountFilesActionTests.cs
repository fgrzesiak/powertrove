using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PowerTrove.Abstractions;
using PowerTrove.Pack.Files;
using Xunit;

namespace PowerTrove.Pack.Files.Tests;

public class CountFilesActionTests
{
    private class FakeDirectoryScanner : IDirectoryScanner
    {
        public DirectoryScanResult EnumerateFiles(string path, int maxDepth, int maxItems, bool followSymlinks = false)
        {
            return new DirectoryScanResult(new List<string> { "a", "b", "c" }, false, 3);
        }
    }

    [Fact]
    public async Task PreviewAsync_ShowsBoundedItemCount()
    {
        // Arrange
        var scanner = new FakeDirectoryScanner();
        var action = new CountFilesAction(scanner);

        var selection = new FileSelection(new[]
        {
            new SelectedFile("C:\\Folder", "Folder", "", "directory", null, true, true, null, true)
        });

        var snapshot = new ContextSnapshot(
            Guid.NewGuid(),
            new SourceWindowIdentity(IntPtr.Zero, 1234, "explorer", DateTimeOffset.UtcNow),
            ContextStatus.Available,
            selection,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(5),
            "test_provider",
            null
        );

        // Act
        var preview = await action.PreviewAsync(snapshot, new EmptyParameters(), CancellationToken.None);

        // Assert
        Assert.Contains("Found 3 items", preview.PreviewText);
    }
}
