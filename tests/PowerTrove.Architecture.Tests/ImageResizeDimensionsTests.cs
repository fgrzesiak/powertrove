using PowerTrove.Abstractions.Testing;
using PowerTrove.Desktop;

namespace PowerTrove.Architecture.Tests;

public sealed class ImageResizeDimensionsTests
{
    [Fact]
    public void WholeImageDimensionsLinkToTheOriginalAspectWithoutAccumulatingRounding()
    {
        var size = new ImageResizeDimensions(300, 200, 300, 200);
        Assert.Equal((1200, 800), (size.SliderWidth, size.SliderHeight));
        Assert.True(size.SetWidth(601)); Assert.Equal((601, 401), (size.Width, size.Height));
        Assert.True(size.SetHeight(200)); Assert.Equal((300, 200), (size.Width, size.Height));
        Assert.True(size.SetScale(2)); Assert.Equal((600, 400), (size.Width, size.Height));
        Assert.True(size.SetScale(.5)); Assert.Equal((150, 100), (size.Width, size.Height));
        Assert.True(size.SetScale(1)); Assert.Equal((300, 200), (size.Width, size.Height));
        Assert.True(size.SetScale(5)); Assert.Equal((1500, 1000), (size.Width, size.Height));
        var thin = new ImageResizeDimensions(100, 1, 100, 1);
        Assert.True(thin.SetScale(.01)); Assert.Equal((1, 1), (thin.Width, thin.Height));
    }
    [Fact]
    public void InvalidSizesNeverChangeTheLastValidDimensions()
    {
        var size = new ImageResizeDimensions(4000, 4000, 4000, 4000);
        Assert.InRange(size.SliderWidth, 4000, 7745);
        Assert.InRange(size.SliderHeight, 4000, 7745);
        Assert.False(size.SetScale(4)); Assert.False(size.SetWidth(0)); Assert.False(size.SetHeight(40000));
        Assert.False(size.SetScale(double.NaN)); Assert.False(size.SetScale(100000)); Assert.Equal((4000, 4000), (size.Width, size.Height));
    }
    [Fact]
    public void ResizeUsesItsSimpleViewAndVectorizationKeepsAnOptionalPreview()
    {
        var one = Snapshots.ForPaths(Path.Combine(Path.GetTempPath(), "generated.png"));
        Assert.True(ImageEffectsInput.OpensDirectly("powertrove.image.resize", one));
        Assert.False(ImageEditorInput.CanOffer("powertrove.image.resize", one));
        Assert.True(ImageEffectsInput.CanOffer("powertrove.image.png-to-svg", one));
        Assert.False(ImageEffectsInput.OpensDirectly("powertrove.image.png-to-svg", one));
        Assert.False(ImageEffectsInput.CanOffer("powertrove.image.resize", one with { Content = new FileSelection([.. ((FileSelection)one.Content).Files, .. ((FileSelection)one.Content).Files]) }));
        Assert.False(ImageEffectsInput.CanOffer("powertrove.image.png-to-svg", Snapshots.ForPaths(Path.Combine(Path.GetTempPath(), "generated.jpg"))));
    }
}
