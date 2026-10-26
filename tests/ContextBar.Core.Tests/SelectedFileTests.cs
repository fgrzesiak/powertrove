using System.Collections.Generic;
using ContextBar.Core;
using Xunit;

namespace ContextBar.Core.Tests;

public class SelectedFileTests
{
    [Fact]
    public void SelectedFile_MixedFileSets_CanBeRepresented()
    {
        // Arrange
        var files = new List<SelectedFile>
        {
            new SelectedFile("C:\\image.png", "image.png", ".png", "image/png", 1024, true, true, null, false),
            new SelectedFile("C:\\document.pdf", "document.pdf", ".pdf", "application/pdf", 2048, true, true, null, false)
        };
        var selection = new FileSelection(files);

        // Assert
        Assert.Equal(2, selection.Files.Count);
        Assert.Equal("image.png", selection.Files[0].Name);
        Assert.Equal(".pdf", selection.Files[1].Extension);
        Assert.Equal("application/pdf", selection.Files[1].MediaType);
    }

    [Fact]
    public void SelectedFile_NullAndInaccessiblePaths_HandledProperly()
    {
        // Arrange
        var virtualFile = new SelectedFile(null, "virtual_doc.txt", ".txt", "text/plain", 500, false, true, null, false);
        var inaccessibleFile = new SelectedFile("D:\\locked.doc", "locked.doc", ".doc", "application/msword", 1000, true, false, null, false);

        var selection = new FileSelection(new[] { virtualFile, inaccessibleFile });

        // Assert
        Assert.Null(selection.Files[0].Path);
        Assert.False(selection.Files[0].IsAvailableLocally);

        Assert.Equal("D:\\locked.doc", selection.Files[1].Path);
        Assert.False(selection.Files[1].IsAccessible);
    }

    [Fact]
    public void SelectedFile_PrivacyBoundaries_ToStringDoesNotLeakPaths()
    {
        // Arrange
        var sensitivePath = "C:\\Users\\SecretUser\\Desktop\\Confidential.txt";
        var sensitiveName = "Confidential.txt";
        var file = new SelectedFile(sensitivePath, sensitiveName, ".txt", "text/plain", 100, true, true, null, false);

        var selection = new FileSelection(new[] { file });

        // Act
        var fileString = file.ToString();
        var selectionString = selection.ToString();

        // Assert
        Assert.DoesNotContain(sensitivePath, fileString);
        Assert.DoesNotContain(sensitiveName, fileString);

        Assert.DoesNotContain(sensitivePath, selectionString);
        Assert.DoesNotContain(sensitiveName, selectionString);

        Assert.Contains("Count = 1", selectionString);
    }
}
