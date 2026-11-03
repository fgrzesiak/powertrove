using PowerTrove.Abstractions.Packs;

namespace PowerTrove.Pack.Markdown.Tests;

public sealed class PackFactoryTests : PackFactoryContractTestsBase
{
    protected override IPackFactory CreateFactory() => new MarkdownPackFactory();
}

public sealed class MarkdownViewActionContract : ActionContractTestsBase, IDisposable
{
    private readonly TempDirectory _dir = new();
    protected override IAction CreateAction() => new MarkdownViewAction(FileActionServices.CreatePhysical(new ExtensionOnlyTypeVerifier()), new Markdig.MarkdigMarkdownEngine(), edit: false);
    protected override ContextSnapshot CreateValidSnapshot() => Snapshots.ForPaths(_dir.Write("readme.md", "# Title\n"));
    public void Dispose() => _dir.Dispose();
}

public sealed class MarkdownToHtmlActionContract : ActionContractTestsBase, IDisposable
{
    private readonly TempDirectory _dir = new();
    protected override IAction CreateAction() => new MarkdownToHtmlAction(FileActionServices.CreatePhysical(new ExtensionOnlyTypeVerifier()), new Markdig.MarkdigMarkdownEngine());
    protected override ContextSnapshot CreateValidSnapshot() => Snapshots.ForPaths(_dir.Write("readme.md", "# Title\n"));
    public void Dispose() => _dir.Dispose();
}

public sealed class ToMarkdownActionContract : ActionContractTestsBase, IDisposable
{
    private readonly TempDirectory _dir = new();
    protected override IAction CreateAction() => new ToMarkdownAction(FileActionServices.CreatePhysical(new ExtensionOnlyTypeVerifier()), new Markdig.MarkdigMarkdownEngine(), null);
    protected override ContextSnapshot CreateValidSnapshot() => Snapshots.ForPaths(_dir.Write("page.html", "<h1>Title</h1>"));
    public void Dispose() => _dir.Dispose();
}
