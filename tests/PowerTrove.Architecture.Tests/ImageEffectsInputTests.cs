using PowerTrove.Abstractions.Testing;
using PowerTrove.Desktop;

namespace PowerTrove.Architecture.Tests;

public sealed class ImageEffectsInputTests
{
    [Fact]
    public void IconConversionKeepsSimpleOptionsAndOffersTheVisualViewAsAnAlternative()
    {
        var snapshot = Snapshots.ForPaths(Path.Combine(Path.GetTempPath(), "generated.png"));
        Assert.True(ImageEffectsInput.CanOffer("powertrove.image.to-ico", snapshot));
        Assert.False(ImageEffectsInput.OpensDirectly("powertrove.image.to-ico", snapshot));
        Assert.True(ImageEffectsInput.OpensDirectly("powertrove.image.watermark-text", snapshot));
    }
    [Theory]
    [InlineData("watermark-text")]
    [InlineData("watermark-image")]
    [InlineData("crop-aspect")]
    [InlineData("pad")]
    [InlineData("grayscale")]
    [InlineData("optimize-png")]
    [InlineData("to-ico")]
    [InlineData("blur-faces")]
    public void SupportedPerFileActionsCanOpenTheVisualView(string id)
    {
        var snapshot = Snapshots.ForPaths(Path.Combine(Path.GetTempPath(), "generated.png"));
        Assert.True(ImageEffectsInput.CanOffer("powertrove.image." + id, snapshot));
        Assert.False(ImageEffectsInput.CanOffer("powertrove.image.remove-metadata", snapshot));
        Assert.False(ImageEffectsInput.CanOffer("powertrove.image." + id, Snapshots.ForPaths()));
    }

    [Fact]
    public void LayoutLimitsAndNonLocalInputsAreRejectedBeforeOpeningAWindow()
    {
        string[] paths = Enumerable.Range(0, 51).Select(i => Path.Combine(Path.GetTempPath(), $"generated{i}.png")).ToArray();
        var snapshot = Snapshots.ForPaths(paths);
        Assert.True(ImageEffectsInput.CanOffer("powertrove.image.collage", snapshot));
        Assert.False(ImageEffectsInput.CanOffer("powertrove.image.stitch", snapshot));
        var file = ((FileSelection)snapshot.Content).Files[0];
        Assert.False(ImageEffectsInput.CanOffer("powertrove.image.watermark-text", snapshot with { Content = new FileSelection([file with { IsAvailableLocally = false }]) }));
        Assert.False(ImageEffectsInput.CanOffer("powertrove.image.watermark-text", snapshot with { Content = new FileSelection([file with { IsAccessible = false }]) }));
        Assert.False(ImageEffectsInput.CanOffer("powertrove.image.watermark-text", Snapshots.ForPaths(Path.ChangeExtension(paths[0], ".svg"))));
        Assert.False(ImageEffectsInput.CanOffer("powertrove.image.collage", Snapshots.ForPaths(paths[0])));
    }
}
