using PowerTrove.Abstractions.Testing;
using PowerTrove.Application.Commands;
using PowerTrove.Application.Configuration;
using PowerTrove.Application.Packs;
using PowerTrove.Core;
using PowerTrove.Pack.Pdf;

namespace PowerTrove.Desktop.Tests;

public sealed class CoreCompositionTests
{
    [Fact]
    public void OrganizerParameterIsParsedByTheRealPdfPack_IncludingNormalizedRotations()
    {
        string text = OrganizePagesParameter.Format([(0, 2, 0), (0, 0, -270), (1, 1, 540)]);
        Assert.Equal("1:3, 1:1@90, 2:2@180", text);
        Assert.True(PageArrangement.TryParse(text, out var pages, out var error), error);
        Assert.Equal([new ArrangedPage(0, 2, 0), new ArrangedPage(0, 0, 90), new ArrangedPage(1, 1, 180)], pages);
    }

    [Theory]
    [InlineData("webp", 70, 1200, 800, "powertrove.image.convert-webp", "1200", "800")]
    [InlineData("jpg", 90, 1200, null, "powertrove.image.convert-jpeg", "0", "0")]
    [InlineData("png", 50, null, null, "powertrove.image.convert-png", "0", "0")]
    public async Task SavedPresetsRunTheInstalledConversionWithFixedValues(string format, int quality, int? width, int? height,
        string target, string expectedWidth, string expectedHeight)
    {
        using var temp = new TempDirectory();
        var registry = new ActionRegistry();
        foreach (var pack in BuiltInActionPacks.Create(new PowerTrove.Core.Workflows.WorkflowRunner(registry)))
            registry.RegisterPack(pack.Info.Id, pack.Actions);
        var preset = new ImagePresetAction(new SavedImageExportPreset("user.preset.web", "Web", "Generated preset", format, quality, width, height, true, true),
            registry.GetAction);
        var values = preset.Parameters().Values;
        Assert.Equal((expectedWidth, expectedHeight, "true"), (values["MaxWidth"], values["MaxHeight"], values["StripMetadata"]));
        Assert.Equal(format == "png" ? null : quality.ToString(System.Globalization.CultureInfo.InvariantCulture), values.GetValueOrDefault("Quality"));
        // The fixed values are exactly the parameters the installed conversion declares.
        var declared = registry.GetAction(target)!.Descriptor.Parameters!.Select(p => p.Name).ToHashSet();
        Assert.Subset(declared, values.Keys.ToHashSet());
        string image = temp.Write("photo.png", Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/iZk9HQAAAABJRU5ErkJggg=="));
        Assert.True((await preset.MatchAsync(Snapshots.ForPaths(image), default)).IsAvailable);
    }

    [Fact]
    public async Task PresetsWithoutAnInstalledImagesPackExplainTheMissingPackInsteadOfRunning()
    {
        var registry = new ActionRegistry();
        var preset = new ImagePresetAction(new SavedImageExportPreset("user.preset.web", "Web", "Generated preset", "webp", 80, null, null, true, true),
            registry.GetAction);
        using var temp = new TempDirectory();
        var snapshot = Snapshots.ForPaths(temp.Write("photo.png", "not inspected"));
        Assert.False((await preset.MatchAsync(snapshot, default)).IsAvailable);
        Assert.True((await preset.PreviewAsync(snapshot, new EmptyParameters(), default)).IsBlocked);
        Assert.False((await preset.ExecuteAsync(snapshot, new EmptyParameters(), null, default)).IsSuccess);
    }
}
