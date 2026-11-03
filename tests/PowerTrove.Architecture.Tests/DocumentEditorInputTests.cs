using PowerTrove.Desktop;
using PowerTrove.Pack.Markdown;

namespace PowerTrove.Architecture.Tests;

public sealed class DocumentEditorInputTests
{
    [Fact]
    public void TheHostOpensExactlyTheMarkdownViewerActionsInItsEditor()
    {
        Assert.Equal(MarkdownViewAction.ViewId, DocumentEditorInput.MarkdownViewActionId);
        Assert.Equal(MarkdownViewAction.EditId, DocumentEditorInput.MarkdownEditActionId);
        Assert.False(DocumentEditorInput.OpensEditor(MarkdownViewAction.ViewId));
        Assert.True(DocumentEditorInput.OpensEditor(MarkdownViewAction.EditId));
        var pack = new MarkdownPackFactory().Create(PackFactoriesTests.Host());
        Assert.All(pack.Actions.Where(a => a.Descriptor.Id is not (MarkdownViewAction.ViewId or MarkdownViewAction.EditId)),
            a => Assert.Null(DocumentEditorInput.OpensEditor(a.Descriptor.Id)));
        // Only actions that never write themselves are routed to the editor, which writes on an explicit Save.
        Assert.Contains(pack.Actions, a => a.Descriptor.Id == MarkdownViewAction.ViewId && !a.Descriptor.ChangesFiles);
    }
}
