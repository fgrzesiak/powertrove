using System;
using System.Collections.Generic;
using Xunit;

namespace FileKit.Pdf.Tests;

public class PdfOutlineRemapTests
{
    [Fact]
    public void Remap_KeepsSurvivingItems()
    {
        var items = new List<PdfOutlineItem>
        {
            new("Keep 1", 0, Array.Empty<PdfOutlineItem>()),
            new("Keep 2", 1, Array.Empty<PdfOutlineItem>())
        };

        var remapped = PdfOutline.Remap(items, idx => idx == 0 ? 0 : 1);

        Assert.Equal(2, remapped.Count);
        Assert.Equal("Keep 1", remapped[0].Title);
        Assert.Equal(0, remapped[0].PageIndex);
        Assert.Equal("Keep 2", remapped[1].Title);
        Assert.Equal(1, remapped[1].PageIndex);
    }

    [Fact]
    public void Remap_DropsUnmappedItems()
    {
        var items = new List<PdfOutlineItem>
        {
            new("Keep 1", 0, Array.Empty<PdfOutlineItem>()),
            new("Drop 1", 1, Array.Empty<PdfOutlineItem>()),
            new("Keep 2", 2, Array.Empty<PdfOutlineItem>())
        };

        var remapped = PdfOutline.Remap(items, idx => idx == 1 ? null : (idx == 0 ? 0 : 1));

        Assert.Equal(2, remapped.Count);
        Assert.Equal("Keep 1", remapped[0].Title);
        Assert.Equal(0, remapped[0].PageIndex);
        Assert.Equal("Keep 2", remapped[1].Title);
        Assert.Equal(1, remapped[1].PageIndex);
    }

    [Fact]
    public void Remap_PromotesSurvivingChildrenOfDroppedParent()
    {
        var children = new List<PdfOutlineItem>
        {
            new("Child 1 (Keep)", 1, Array.Empty<PdfOutlineItem>()),
            new("Child 2 (Drop)", 2, Array.Empty<PdfOutlineItem>()),
            new("Child 3 (Keep)", 3, Array.Empty<PdfOutlineItem>())
        };

        var items = new List<PdfOutlineItem>
        {
            new("Root (Drop)", 0, children)
        };

        var remapped = PdfOutline.Remap(items, idx =>
        {
            if (idx == 0 || idx == 2) return null;
            if (idx == 1) return 0;
            if (idx == 3) return 1;
            return null;
        });

        Assert.Equal(2, remapped.Count);
        Assert.Equal("Child 1 (Keep)", remapped[0].Title);
        Assert.Equal(0, remapped[0].PageIndex);
        Assert.Equal("Child 3 (Keep)", remapped[1].Title);
        Assert.Equal(1, remapped[1].PageIndex);
    }

    [Fact]
    public void Remap_PreservesChildrenHierarchyIfParentSurvives()
    {
        var grandChildren = new List<PdfOutlineItem>
        {
            new("GrandChild", 2, Array.Empty<PdfOutlineItem>())
        };

        var children = new List<PdfOutlineItem>
        {
            new("Child", 1, grandChildren)
        };

        var items = new List<PdfOutlineItem>
        {
            new("Root", 0, children)
        };

        var remapped = PdfOutline.Remap(items, idx => idx);

        Assert.Single(remapped);
        Assert.Equal("Root", remapped[0].Title);
        Assert.Single(remapped[0].Children);
        Assert.Equal("Child", remapped[0].Children[0].Title);
        Assert.Single(remapped[0].Children[0].Children);
        Assert.Equal("GrandChild", remapped[0].Children[0].Children[0].Title);
    }

    [Fact]
    public void Remap_NoDest_KeepsIfChildrenSurvive()
    {
        var children = new List<PdfOutlineItem>
        {
            new("Child", 0, Array.Empty<PdfOutlineItem>())
        };

        var items = new List<PdfOutlineItem>
        {
            new("Folder", null, children)
        };

        var remapped = PdfOutline.Remap(items, idx => idx);

        Assert.Single(remapped);
        Assert.Equal("Folder", remapped[0].Title);
        Assert.Null(remapped[0].PageIndex);
        Assert.Single(remapped[0].Children);
        Assert.Equal("Child", remapped[0].Children[0].Title);
        Assert.Equal(0, remapped[0].Children[0].PageIndex);
    }
}
