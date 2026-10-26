using System.Text;
using Xunit;

namespace FileKit.Pdf.Tests;

public class PdfDocumentBuilderTests
{
    private static PdfDocument Reload(MemoryStream output) => PdfDocument.Load(output.ToArray());

    [Fact]
    public void AddBlankPage_CreatesEmptyPageWithCorrectDimensions()
    {
        var builder = new PdfDocumentBuilder();
        builder.AddBlankPage(200, 300);

        using var output = new MemoryStream();
        builder.Save(output);

        var doc = Reload(output);
        Assert.Equal(1, doc.PageCount);

        var page = doc.Pages[0];
        Assert.NotNull(page.MediaBox);

        var mediaBox = doc.Resolve(page.MediaBox) as PdfArray;
        Assert.NotNull(mediaBox);
        Assert.Equal(4, mediaBox.Count);

        Assert.Equal(0, ((PdfInteger)doc.Resolve(mediaBox.Items[0])).Value);
        Assert.Equal(0, ((PdfInteger)doc.Resolve(mediaBox.Items[1])).Value);
        Assert.Equal(200, doc.Resolve(mediaBox.Items[2]) is PdfReal r2 ? r2.Value : ((PdfInteger)doc.Resolve(mediaBox.Items[2])).Value);
        Assert.Equal(300, doc.Resolve(mediaBox.Items[3]) is PdfReal r3 ? r3.Value : ((PdfInteger)doc.Resolve(mediaBox.Items[3])).Value);

        Assert.NotNull(page.Resources);
        var resources = doc.Resolve(page.Resources) as PdfDictionary;
        Assert.NotNull(resources);
        Assert.Empty(resources.Entries);
    }
}
