using PowerTrove.Abstractions.Files;
using PowerTrove.Abstractions.Testing;
using PowerTrove.Core;
using PowerTrove.Core.Search;
using PowerTrove.Pack.Pdf;

namespace PowerTrove.Architecture.Tests;

public sealed class SelectionRankingIntegrationTests
{
    [Theory]
    [InlineData(false, "powertrove.pdf.stamp-selected-image")]
    [InlineData(true, "powertrove.pdf.combine")]
    public async Task PdfAndImage_UseProductionDescriptorsWithNeutralUsage(bool extraPdf, string expectedFirst)
    {
        var services = FileActionServices.CreatePhysical(new ExtensionOnlyTypeVerifier());
        var registry = new ActionRegistry();
        // These actions need no processing engines for metadata-only matching.
        registry.Register(new StampSelectedImagePdfAction(services));
        registry.Register(new CombineToPdfAction(services));
        var files = new List<SelectedFile>
        {
            File("input.pdf"), File("input.jpg")
        };
        if (extraPdf) files.Add(File("other.pdf"));
        var now = DateTimeOffset.UtcNow;
        var context = new ContextSnapshot(Guid.NewGuid(), new SourceWindowIdentity(IntPtr.Zero, 1, "test", now),
            ContextStatus.Available, new FileSelection(files), now, now.AddMinutes(5), "test", null);

        var results = await new ActionSearchEngine(registry).SearchAsync("", context);

        Assert.Equal(expectedFirst, results[0].Action.Descriptor.Id);
        if (!extraPdf)
        {
            Assert.Equal("powertrove.pdf.combine", results[1].Action.Descriptor.Id);
            Assert.Equal(results[0].Rank, results[1].Rank);
        }
    }

    private static SelectedFile File(string path) =>
        new(path, path, Path.GetExtension(path), null, 100, true, true, null);
}
