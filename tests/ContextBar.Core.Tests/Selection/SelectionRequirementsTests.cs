using System;
using System.Collections.Generic;
using ContextBar.Abstractions;
using ContextBar.Core.Selection;
using Xunit;

namespace ContextBar.Core.Tests.Selection;

public class SelectionRequirementsTests
{
    private static SelectedFile CreateFile(string name, string extension) => new SelectedFile($"C:\\fake\\{name}{extension}", $"{name}{extension}", extension, null, null, true, true, null, false);

    private static ContextSnapshot CreateFileSelection(params SelectedFile[] files) =>
        new ContextSnapshot(
            Guid.NewGuid(),
            new SourceWindowIdentity((nint)123, 456, "test", DateTimeOffset.UtcNow),
            ContextStatus.Available,
            new FileSelection(files),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(5),
            "ProviderId",
            null
        );

    [Theory]
    [InlineData("doc1.pdf", SelectionFit.Ideal)]
    [InlineData("doc1.pdf,doc2.pdf", SelectionFit.None)]
    [InlineData("image.png", SelectionFit.None)]
    [InlineData("doc1.pdf,image.png", SelectionFit.None)]
    public void Evaluate_SinglePdf(string filesStr, SelectionFit expected)
    {
        var req = SelectionRequirements.Single("pdf");
        var s = CreateSelectionFromString(filesStr);
        Assert.Equal(expected, SelectionRequirements.Evaluate(req, s));
    }

    [Theory]
    [InlineData("doc1.pdf", SelectionFit.None)]
    [InlineData("doc1.pdf,doc2.pdf", SelectionFit.Ideal)]
    [InlineData("doc1.pdf,doc2.pdf,doc3.pdf", SelectionFit.Ideal)]
    [InlineData("doc1.pdf,image.png", SelectionFit.None)]
    public void Evaluate_MultiplePdf(string filesStr, SelectionFit expected)
    {
        var req = SelectionRequirements.Multiple("pdf");
        var s = CreateSelectionFromString(filesStr);
        Assert.Equal(expected, SelectionRequirements.Evaluate(req, s));
    }

    [Theory]
    [InlineData("doc1.pdf", SelectionFit.Ideal)]
    [InlineData("doc1.pdf,doc2.pdf", SelectionFit.Ideal)]
    [InlineData("image.png", SelectionFit.None)]
    [InlineData("doc1.pdf,image.png", SelectionFit.Acceptable)]
    public void Evaluate_AnyPdf(string filesStr, SelectionFit expected)
    {
        var req = SelectionRequirements.Any("pdf");
        var s = CreateSelectionFromString(filesStr);
        Assert.Equal(expected, SelectionRequirements.Evaluate(req, s));
    }

    [Theory]
    [InlineData("doc1.pdf,image.png", SelectionFit.Acceptable)]
    [InlineData("image.png,image2.png", SelectionFit.Ideal)]
    [InlineData("doc1.pdf", SelectionFit.None)]
    public void Evaluate_MixedSelectionsWithCustomRequirements(string filesStr, SelectionFit expected)
    {
        var req = new SelectionRequirements(new HashSet<string>{ "image" }, MinItems: 1, MaxItems: null, AllowOtherItems: true, IdealMinItems: 2, IdealMaxItems: null);
        var s = CreateSelectionFromString(filesStr);
        Assert.Equal(expected, SelectionRequirements.Evaluate(req, s));
    }

    [Theory]
    [InlineData("doc1.pdf", SelectionFit.Ideal)]
    [InlineData("image.png", SelectionFit.Ideal)]
    [InlineData("doc1.pdf,image.png", SelectionFit.Ideal)]
    [InlineData("doc1.pdf,folder1.folder", SelectionFit.Ideal)]
    public void Evaluate_AnyFile(string filesStr, SelectionFit expected)
    {
        var req = SelectionRequirements.Any(); // Empty families = any
        var s = CreateSelectionFromString(filesStr);
        Assert.Equal(expected, SelectionRequirements.Evaluate(req, s));
    }

    [Theory]
    [InlineData("doc1.pdf,image.png", SelectionFit.Ideal)]
    [InlineData("doc1.pdf,doc2.pdf,image.png", SelectionFit.None)] // Too many pdfs
    [InlineData("doc1.pdf", SelectionFit.None)] // Missing image
    [InlineData("image.png", SelectionFit.None)] // Missing pdf
    [InlineData("doc1.pdf,image.png,text.txt", SelectionFit.Acceptable)] // Extra files allowed but not ideal
    public void Evaluate_FamilyLimits(string filesStr, SelectionFit expected)
    {
        var req = new SelectionRequirements(
            new HashSet<string>(new[] { "pdf", "image" }, StringComparer.OrdinalIgnoreCase),
            MinItems: 2,
            IdealMinItems: 2,
            AllowOtherItems: true,
            FamilyLimits: new Dictionary<string, FamilyLimit>(StringComparer.OrdinalIgnoreCase)
            {
                { "pdf", new FamilyLimit(1, 1) },
                { "image", new FamilyLimit(1, 1) }
            });

        var s = CreateSelectionFromString(filesStr);
        Assert.Equal(expected, SelectionRequirements.Evaluate(req, s));
    }

    private static ContextSnapshot CreateSelectionFromString(string filesStr)
    {
        var parts = filesStr.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var files = new List<SelectedFile>();
        foreach(var part in parts)
        {
            int idx = part.LastIndexOf('.');
            if (idx == -1 && part.EndsWith(".folder"))
            {
                files.Add(new SelectedFile($"C:\\fake\\{part}", part, "", null, null, true, true, null, true));
            }
            else
            {
                string ext = idx >= 0 ? part.Substring(idx) : "";
                string name = idx >= 0 ? part.Substring(0, idx) : part;
                files.Add(CreateFile(name, ext));
            }
        }
        return CreateFileSelection(files.ToArray());
    }
}
