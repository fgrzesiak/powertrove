namespace ContextBar.Abstractions.Tests;

public sealed class OfficeSelectionFitTests
{
    [Theory]
    [InlineData("a.docx", "b.pdf", SelectionFit.Ideal)]
    [InlineData("a.pptx", "b.jpg", SelectionFit.Ideal)]
    [InlineData("a.xlsx", "b.pdf", SelectionFit.Ideal)]
    [InlineData("a.pdf", "b.pdf", SelectionFit.Acceptable)]
    [InlineData("a.jpg", "b.png", SelectionFit.Acceptable)]
    [InlineData("a.pdf", "b.txt", SelectionFit.None)]
    public void MinimumDistinctFamilies_PreservesPureAndMixedSelectionFit(string first, string second, SelectionFit expected)
    {
        var requirements = new SelectionRequirements(new HashSet<string> { "pdf", "image", "document", "spreadsheet" },
            MinItems: 2, AllowOtherItems: false, IdealMinItems: 2, IdealMinDistinctFamilies: 2);
        var files = new[] { first, second }.Select(path => new SelectedFile(path, path, Path.GetExtension(path), null, null, true, true, null)).ToArray();
        var now = DateTimeOffset.UtcNow;
        var snapshot = new ContextSnapshot(Guid.NewGuid(), new SourceWindowIdentity(0, 1, "test", now), ContextStatus.Available, new FileSelection(files), now, now.AddMinutes(1), "test", null);
        Assert.Equal(expected, SelectionRequirements.Evaluate(requirements, snapshot));
    }
}
