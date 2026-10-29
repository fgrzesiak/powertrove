using FileKit.Imaging.Raster;
using PowerTrove.Desktop;
using PowerTrove.Abstractions.Testing;
using PowerTrove.Abstractions.Files;

namespace PowerTrove.Architecture.Tests;

public sealed class ImageEditorInputTests
{
    [Theory]
    [InlineData("powertrove.image.rotate-right")]
    [InlineData("powertrove.image.rotate-left")]
    [InlineData("powertrove.image.rotate-180")]
    public void RasterGeometryActionsOpenTheDragEditorDirectlyForASingleLocalImage(string id)
    {
        var snapshot = Snapshots.ForPaths(Path.Combine(Path.GetTempPath(), "generated.png"));
        Assert.True(ImageEditorInput.OpensDirectly(id, snapshot));
        var file = ((FileSelection)snapshot.Content).Files[0];
        Assert.False(ImageEditorInput.OpensDirectly(id, snapshot with { Content = new FileSelection([file, file]) }));
        Assert.False(ImageEditorInput.OpensDirectly(id, snapshot with { Content = new FileSelection([file with { IsAccessible = false }]) }));
        Assert.False(ImageEffectsInput.CanOffer(id, snapshot));
    }

    [Theory]
    [InlineData("powertrove.image.pad")]
    [InlineData("powertrove.image.crop-aspect")]
    public void BordersAndCroppingOpenTheirOwnRenderedEditorToRetainColorAndFormat(string id)
    {
        var snapshot = Snapshots.ForPaths(Path.Combine(Path.GetTempPath(), "generated.png"));
        Assert.True(ImageEffectsInput.CanOffer(id, snapshot));
        Assert.False(ImageEditorInput.OpensDirectly(id, snapshot));
    }

    [Theory]
    [InlineData("powertrove.image.svg-to-png", false)]
    [InlineData("powertrove.image.edit", true)]
    [InlineData("powertrove.image.resize", false)]
    [InlineData("powertrove.image.convert-png", false)]
    public void ALocalSingleSvgOpensTheCanvasDirectlyWithoutChangingOtherConversionRoutes(string action, bool expected)
    {
        var snapshot = Snapshots.ForPaths(Path.Combine(Path.GetTempPath(), "generated.svg"));
        Assert.Equal(expected, ImageEditorInput.OpensDirectly(action, snapshot));
        if (action == "powertrove.image.svg-to-png") Assert.True(ImageEditorInput.CanOffer(action, snapshot));
        var file = ((FileSelection)snapshot.Content).Files[0];
        Assert.False(ImageEditorInput.OpensDirectly(action, snapshot with { Content = new FileSelection([file, file]) }));
        Assert.False(ImageEditorInput.OpensDirectly(action, snapshot with { Content = new FileSelection([file with { IsAvailableLocally = false }]) }));
        if (action == "powertrove.image.svg-to-png")
            Assert.False(ImageEditorInput.OpensDirectly(action, snapshot with { Content = new FileSelection([file with { Extension = ".png" }]) }));
    }

    [Fact]
    public void DirectSvgEntryCarriesConversionDefaultsAndSavedExactCanvasIntoTheEditor()
    {
        var descriptor = new PowerTrove.Pack.Images.SvgToPngAction(
            FileActionServices.CreatePhysical(new ExtensionOnlyTypeVerifier()), null).Descriptor;
        var defaults = ImageEditorInput.InitialParameters(descriptor, null);
        Assert.Equal(new ImageCanvasLayout(1024, 1024, 1024, 1024), ImageEditorInput.Seed(128, 128, descriptor.Id, defaults.Values));
        var saved = new Dictionary<string, string> { ["Width"] = "1280", ["Height"] = "640", ["ExactCanvas"] = "true", ["Unknown"] = "ignored" };
        var initial = ImageEditorInput.InitialParameters(descriptor, saved);
        Assert.Equal(new ImageCanvasLayout(1280, 640, 640, 640), ImageEditorInput.Seed(128, 128, descriptor.Id, initial.Values));
        Assert.False(initial.Values.ContainsKey("Unknown")); Assert.Equal(4, saved.Count);
        Assert.Equal(defaults.Values, ImageEditorInput.InitialParameters(descriptor with { CanRememberParameters = false }, saved).Values);
    }

    [Fact]
    public void GeometryParametersRoundTripWithoutLosingPositionOrRotation()
    {
        var layout = new ImageCanvasLayout(1280, 640, 500, 300, 42, -20, 37);
        Assert.Equal(layout, ImageEditorInput.Seed(128, 128, ImageEditorInput.ActionId, ImageEditorInput.Parameters(layout).Values));
    }

    [Theory]
    [InlineData("powertrove.image.rotate-right", 90)]
    [InlineData("powertrove.image.rotate-left", -90)]
    public void RotationHandoffRotatesTheCanvasToo(string origin, int angle)
    {
        var layout = ImageEditorInput.Seed(200, 100, origin, new Dictionary<string, string>());
        Assert.Equal((100, 200, angle), (layout.CanvasWidth, layout.CanvasHeight, layout.Angle)); Assert.False(layout.IsClipped);
    }

    [Fact]
    public void ExistingSvgCanvasAndResizeOptionsHaveDistinctInitialLayouts()
    {
        var svg = ImageEditorInput.Seed(128, 128, "powertrove.image.svg-to-png", new Dictionary<string,string> { ["Width"]="1280", ["Height"]="640", ["ExactCanvas"]="true" });
        Assert.Equal(new ImageCanvasLayout(1280, 640, 640, 640), svg);
        var resize = ImageEditorInput.Seed(128, 128, "powertrove.image.resize", new Dictionary<string,string> { ["MaxWidth"]="1280", ["MaxHeight"]="640", ["AllowUpscale"]="false" });
        Assert.Equal(new ImageCanvasLayout(128,128,128,128), resize);
    }

    [Fact]
    public void CropAndBorderOptionsAreCarriedIntoTheVisualLayout()
    {
        var crop = ImageEditorInput.Seed(200,100,"powertrove.image.crop-aspect",new Dictionary<string,string>{["Ratio"]="1:1",["Anchor"]="left"});
        Assert.Equal(new ImageCanvasLayout(100,100,200,100,50), crop); Assert.True(crop.IsClipped);
        var border = ImageEditorInput.Seed(200,100,"powertrove.image.pad",new Dictionary<string,string>{["Mode"]="border",["BorderPx"]="20"});
        Assert.Equal(new ImageCanvasLayout(240,140,200,100), border);
    }
}
