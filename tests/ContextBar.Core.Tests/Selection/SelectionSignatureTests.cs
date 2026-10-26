using System;
using System.Collections.Generic;
using ContextBar.Abstractions;
using ContextBar.Core.Selection;
using Xunit;

namespace ContextBar.Core.Tests.Selection;

public class SelectionSignatureTests
{
    private static ContextSnapshot CreateSnapshot(ContextStatus status, ContextContent content)
    {
        return new ContextSnapshot(
            Guid.NewGuid(),
            new SourceWindowIdentity(IntPtr.Zero, 1234, "test", DateTimeOffset.UtcNow),
            status,
            content,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(5),
            "testProvider",
            null
        );
    }

    [Theory]
    [InlineData(1, CountBucket.One)]
    [InlineData(2, CountBucket.Few)]
    [InlineData(5, CountBucket.Few)]
    [InlineData(6, CountBucket.Several)]
    [InlineData(20, CountBucket.Several)]
    [InlineData(21, CountBucket.Many)]
    public void From_ComputesCorrectCountBucket_ForBoundaries(int count, CountBucket expectedBucket)
    {
        var files = new List<SelectedFile>();
        for (int i = 0; i < count; i++)
        {
            files.Add(new SelectedFile(null, $"file{i}", ".pdf", null, null, true, true, null, false));
        }

        var snapshot = CreateSnapshot(ContextStatus.Available, new FileSelection(files));
        var signature = SelectionSignature.From(snapshot);

        Assert.Equal(ContentKind.Files, signature.Kind);
        Assert.Equal(expectedBucket, signature.Count);
        var expectedFamily = Assert.Single(signature.Families);
        Assert.Equal(SelectionFamily.Pdf, expectedFamily);
    }

    [Fact]
    public void From_MapsTextAndTable_AndNone()
    {
        var textSnapshot = CreateSnapshot(ContextStatus.Available, new TextSelection("hello"));
        var textSignature = SelectionSignature.From(textSnapshot);
        Assert.Equal(ContentKind.Text, textSignature.Kind);
        Assert.Equal("text", textSignature.Key);

        var tableSnapshot = CreateSnapshot(ContextStatus.Available, new TableData(new List<List<CellValue>>(), null, null));
        var tableSignature = SelectionSignature.From(tableSnapshot);
        Assert.Equal(ContentKind.Table, tableSignature.Kind);
        Assert.Equal("table", tableSignature.Key);

        var noneSnapshot = CreateSnapshot(ContextStatus.Empty, new NoContent());
        var noneSignature = SelectionSignature.From(noneSnapshot);
        Assert.Equal(ContentKind.None, noneSignature.Kind);
        Assert.Equal("none", noneSignature.Key);

        var unavailableFileSnapshot = CreateSnapshot(ContextStatus.Empty, new FileSelection(new List<SelectedFile> { new SelectedFile(null, "f", ".pdf", null, null, false, false, null) }));
        var unavailableFileSignature = SelectionSignature.From(unavailableFileSnapshot);
        Assert.Equal(ContentKind.None, unavailableFileSignature.Kind);
        Assert.Equal("none", unavailableFileSignature.Key);
    }

    [Fact]
    public void Key_FormatsCorrectlyAndSortsFamilies()
    {
        var files = new List<SelectedFile>
        {
            new SelectedFile(null, "1", ".jpg", null, null, true, true, null, false),
            new SelectedFile(null, "2", ".png", null, null, true, true, null, false),
            new SelectedFile(null, "3", ".pdf", null, null, true, true, null, false),
            new SelectedFile(null, "4", ".pdf", null, null, true, true, null, false)
        };

        var snapshot = CreateSnapshot(ContextStatus.Available, new FileSelection(files));
        var signature = SelectionSignature.From(snapshot);

        Assert.Equal(CountBucket.Few, signature.Count);
        Assert.True(signature.IsMixed);

        // Sorting by enum value: Pdf (0), Image (1)
        Assert.Equal(2, signature.Families.Count);
        Assert.Equal(SelectionFamily.Pdf, signature.Families[0]);
        Assert.Equal(SelectionFamily.Image, signature.Families[1]);

        Assert.Equal("files:pdf+image:few", signature.Key);
    }

    [Fact]
    public void Key_DoesNotContainFileNames()
    {
        var secretName = "TopSecretReport.pdf";
        var secretPath = "C:\\Users\\Bob\\Documents\\TopSecretReport.pdf";

        var files = new List<SelectedFile>
        {
            new SelectedFile(secretPath, secretName, ".pdf", null, null, true, true, null, false)
        };

        var snapshot = CreateSnapshot(ContextStatus.Available, new FileSelection(files));
        var signature = SelectionSignature.From(snapshot);

        var key = signature.Key;

        Assert.DoesNotContain(secretName, key, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TopSecretReport", key, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secretPath, key, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Bob", key, StringComparison.OrdinalIgnoreCase);
    }
}
