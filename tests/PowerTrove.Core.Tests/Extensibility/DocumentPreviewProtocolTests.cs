using PowerTrove.Core.Extensibility.PackProtocol;

namespace PowerTrove.Core.Tests.Extensibility;

public sealed class DocumentPreviewProtocolTests
{
    private static readonly FileSelectionPayload Selection = new([new ProtocolFile(@"C:\docs\readme.md", "readme.md", ".md", false, 10)]);

    [Fact]
    public void DraftTextCrossesTheWorkerBoundaryInPreviewRequests()
    {
        // Non-ASCII and HTML-sensitive characters are escaped to six bytes each: the worst case must still fit.
        string draft = string.Concat(Enumerable.Repeat("# Ü<b>&\n", DocumentPreviewRequest.MaximumDraftLength / 8));
        var request = DocumentPreviewRequest.Create(draft: draft);
        var message = new PreviewMessage(Guid.NewGuid(), "powertrove.markdown.edit", Selection, request.Values, DateTimeOffset.UtcNow.AddMinutes(1));
        var restored = Assert.IsType<PreviewMessage>(PackMessageSerializer.DeserializeLine(PackMessageSerializer.SerializeLine(message)));
        Assert.Equal(draft, DocumentPreviewRequest.Draft(new DictionaryParameters(restored.Parameters)));
    }

    [Fact]
    public void ExecuteRequestsNeverCarryDraftText()
    {
        var values = DocumentPreviewRequest.Create(draft: "# Draft").Values;
        var message = new ExecuteMessage(Guid.NewGuid(), "powertrove.markdown.edit", Selection, values, DateTimeOffset.UtcNow.AddMinutes(1), @"C:\staging\x");
        Assert.Throws<PackProtocolException>(() => PackMessageSerializer.SerializeLine(message));
    }

    [Fact]
    public void OrdinaryParametersKeepTheirShortLimit()
    {
        var values = new Dictionary<string, string> { ["Option"] = new string('x', PackProtocolLimits.MaximumTextLength + 1) };
        var message = new PreviewMessage(Guid.NewGuid(), "a.b", Selection, values, DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.Throws<PackProtocolException>(() => PackMessageSerializer.SerializeLine(message));
    }

    [Fact]
    public void RenderedDocumentSurvivesTheWorkerBoundaryAtItsLimit()
    {
        string html = "<p>" + new string('<', DocumentPreviewData.MaximumHtmlLength - 10) + "</p>";
        var preview = new ActionPreview(false, null, [], []) { DocumentPreview = new DocumentPreviewData(html, "Title") };
        var wire = PackMessageSerializer.SerializeLine(PreviewResultMessage.FromPreview(Guid.NewGuid(), preview));
        var restored = Assert.IsType<PreviewResultMessage>(PackMessageSerializer.DeserializeLine(wire)).ToPreview();
        Assert.Equal(preview.DocumentPreview, restored.DocumentPreview);
    }

    [Fact]
    public void OversizedRenderedDocumentsAreRefused()
    {
        var preview = new ActionPreview(false, null, [], []) { DocumentPreview = new DocumentPreviewData(new string('x', DocumentPreviewData.MaximumHtmlLength + 1)) };
        Assert.Throws<PackProtocolException>(() => PackMessageSerializer.SerializeLine(PreviewResultMessage.FromPreview(Guid.NewGuid(), preview)));
    }
}
