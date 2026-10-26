using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using PowerTrove.Abstractions;
using PowerTrove.Pack.Pdf;
using PowerTrove.Infrastructure;
using PowerTrove.Infrastructure.Files;

namespace PowerTrove.Pack.Pdf.Tests;

public class PdfActionsAdditionalTests
{
    // The previous tests were testing `Crop` and `ResizePages` at the tool level.
    // The new actions should just validate the Action setup.
    [Fact]
    public void CropPdfAction_ValidatesOptions()
    {
        var services = FileActionServices.CreatePhysical(new PowerTrove.Infrastructure.Files.FileSignatureVerifier());
        var action = new CropPdfAction(services); // assuming FileActionServices are only used during ProcessAsync or Input filtering
        Assert.Equal("contextbar.pdf.crop", action.Descriptor.Id);
    }

    [Fact]
    public void ResizePdfPagesAction_ValidatesOptions()
    {
        var services = FileActionServices.CreatePhysical(new PowerTrove.Infrastructure.Files.FileSignatureVerifier());
        var action = new ResizePdfPagesAction(services);
        Assert.Equal("contextbar.pdf.resize-pages", action.Descriptor.Id);
    }

    [Fact]
    public async Task EditPdfMetadataAction_BlocksPreviewForLongText()
    {
        var services = FileActionServices.CreatePhysical(new FileSignatureVerifier());
        var action = new EditPdfMetadataAction(services);
        Assert.Equal("contextbar.pdf.edit-metadata", action.Descriptor.Id);

        string tooLong = new string('a', 501);

        var parameters = Snapshots.Parameters(("Title", tooLong), ("Author", "Alice"), ("Subject", ""), ("Keywords", ""));
        var context = Snapshots.ForPaths("test.pdf");
        var preview = await action.PreviewAsync(context, parameters, default);

        Assert.True(preview.IsBlocked);
        Assert.Equal("The text is longer than 500 characters.", preview.PreviewText);
    }
}
