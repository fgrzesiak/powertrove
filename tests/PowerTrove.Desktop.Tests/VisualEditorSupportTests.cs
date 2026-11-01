using PowerTrove.Application.Commands;

namespace PowerTrove.Desktop.Tests;

/// <summary>
/// Visual editors fall back to the options page when the installed pack renders no preview, for example
/// Image Tools 0.1.2, which predates the visual editors of desktop 0.3.2.
/// </summary>
public sealed class VisualEditorSupportTests
{
    private static ActionPreview Preview(bool blocked = false, ImagePreviewData? image = null) =>
        new(false, "Will combine 4 images.", [], [], IsBlocked: blocked) { ImagePreview = image };

    [Fact]
    public void LacksImage_OnlyForAValidPreviewWithoutImage()
    {
        Assert.True(VisualEditorSupport.LacksImage(Preview()));
        // A blocked preview explains a problem with the selection or options; the editor shows that text instead.
        Assert.False(VisualEditorSupport.LacksImage(Preview(blocked: true)));
        Assert.False(VisualEditorSupport.LacksImage(null));
        Assert.False(VisualEditorSupport.LacksImage(Preview(image: new ImagePreviewData(10, 10, 10, 10, new string('A', 40)))));
    }

    [Fact]
    public void MarkedActionsLoseTheirEditorUntilThePackReloads()
    {
        var support = new VisualEditorSupport();
        var installed = new OpenSettingsAction(() => { });
        var reloaded = new OpenSettingsAction(() => { });

        Assert.True(support.IsSupported(installed));
        support.MarkUnsupported(installed);

        Assert.False(support.IsSupported(installed));
        // An updated pack registers new action instances and gets its editor back.
        Assert.True(support.IsSupported(reloaded));
        Assert.False(support.IsSupported(null));
    }
}
