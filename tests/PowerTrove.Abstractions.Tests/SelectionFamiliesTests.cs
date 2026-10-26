using System;
using PowerTrove.Abstractions;
using Xunit;

namespace PowerTrove.Abstractions.Tests;

public class SelectionFamiliesTests
{
    [Theory]
    [InlineData(".pDf", "pdf")]
    [InlineData(".JPG", "image")]
    [InlineData(".jpeg", "image")]
    [InlineData(".mP4", "video")]
    [InlineData(".ziP", "archive")]
    [InlineData(".DoCx", "document")]
    [InlineData(".TxT", "text")]
    [InlineData(".CsV", "spreadsheet")]
    [InlineData(".mp3", "audio")]
    [InlineData(".SRT", "subtitle")]
    [InlineData(".vtt", "subtitle")]
    [InlineData("", "other")]
    [InlineData(null, "other")]
    [InlineData(".xyz", "other")]
    public void FromExtension_ReturnsExpectedFamily_CaseInsensitive(string extension, string expectedFamily)
    {
        var family = SelectionFamilies.FromExtension(extension);
        Assert.Equal(expectedFamily, family);
    }

    [Fact]
    public void Of_ReturnsFolder_ForDirectory()
    {
        var selectedFile = new SelectedFile(null, "MyFolder", "", null, null, true, true, null, true);
        var family = SelectionFamilies.Of(selectedFile);
        Assert.Equal("folder", family);
    }

    [Fact]
    public void Of_ReturnsExtensionFamily_ForFile()
    {
        var selectedFile = new SelectedFile(null, "MyFile", ".pdf", null, null, true, true, null, false);
        var family = SelectionFamilies.Of(selectedFile);
        Assert.Equal("pdf", family);
    }
}
