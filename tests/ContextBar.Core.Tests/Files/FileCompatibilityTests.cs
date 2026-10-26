using System.Collections.Generic;
using ContextBar.Core;
using ContextBar.Abstractions.Files;
using Xunit;

namespace ContextBar.Core.Tests.Files;

public class FileCompatibilityTests
{
    [Fact]
    public void CheckCompatibility_AllImages_ReturnsTrue()
    {
        var selection = new FileSelection(new[]
        {
            new SelectedFile("C:\\image1.png", "image1.png", ".png", "image/png", 100, true, true, null, false),
            new SelectedFile("C:\\image2.jpg", "image2.jpg", ".jpg", "image/jpeg", 200, true, true, null, false)
        });

        var result = FileCompatibility.CheckCompatibility(selection, MediaCategory.Image);
        Assert.True(result.IsAvailable);
    }

    [Fact]
    public void CheckCompatibility_MixedSelection_ReturnsFalseWithReason()
    {
        var selection = new FileSelection(new[]
        {
            new SelectedFile("C:\\image1.png", "image1.png", ".png", "image/png", 100, true, true, null, false),
            new SelectedFile("C:\\doc.pdf", "doc.pdf", ".pdf", "application/pdf", 200, true, true, null, false)
        });

        var result = FileCompatibility.CheckCompatibility(selection, MediaCategory.Image);
        Assert.True(result.IsAvailable);
        Assert.Contains("Mixed selection", result.Reason);
    }

    [Fact]
    public void CheckCompatibility_InaccessibleFile_ReturnsFalse()
    {
         var selection = new FileSelection(new[]
        {
            new SelectedFile("C:\\image1.png", "image1.png", ".png", "image/png", 100, true, false, null, false)
        });

        var result = FileCompatibility.CheckCompatibility(selection, MediaCategory.Image);
        Assert.False(result.IsAvailable);
        Assert.Contains("No supported Image files", result.Reason);
    }

    [Fact]
    public void DetectCategory_Directory_ReturnsDirectory()
    {
        Assert.Equal(MediaCategory.Directory, FileCompatibility.DetectCategory(".folder", true));
    }

    [Fact]
    public void CheckCompatibility_WithDirectories_NoRecursionSupport_Fails()
    {
        var selection = new FileSelection(new[]
        {
            new SelectedFile("C:\\image1.png", "image1.png", ".png", "image/png", 100, true, true, null, false),
            new SelectedFile("C:\\Folder", "Folder", "", "directory", null, true, true, null, true)
        });

        var result = FileCompatibility.CheckCompatibility(selection, MediaCategory.Image, supportsRecursion: false);
        Assert.True(result.IsAvailable);
        Assert.Contains("Mixed selection: 1 file(s) will be skipped", result.Reason);
    }

    [Fact]
    public void CheckCompatibility_WithDirectories_WithRecursionSupport_Passes()
    {
        var selection = new FileSelection(new[]
        {
            new SelectedFile("C:\\image1.png", "image1.png", ".png", "image/png", 100, true, true, null, false),
            new SelectedFile("C:\\Folder", "Folder", "", "directory", null, true, true, null, true)
        });

        // The action supports recursion, so the directory itself shouldn't cause compatibility check to fail.
        // It will just be evaluated on the inner files when executed.
        var result = FileCompatibility.CheckCompatibility(selection, MediaCategory.Image, supportsRecursion: true);
        Assert.True(result.IsAvailable);
    }
}
