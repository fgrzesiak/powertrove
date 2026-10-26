namespace PowerTrove.Pack.Tables.Tests;

public sealed class CombineWorkbookContract : ActionContractTestsBase, IDisposable
{
    private readonly TempDirectory _dir = new();
    protected override IAction CreateAction() => new CombineWorkbookAction(FileActionServices.CreatePhysical(new ExtensionOnlyTypeVerifier()));
    protected override ContextSnapshot CreateValidSnapshot() => Snapshots.ForPaths(_dir.Write("a.csv", "a\n"), _dir.Write("b.csv", "b\n"));
    public void Dispose() => _dir.Dispose();
}
