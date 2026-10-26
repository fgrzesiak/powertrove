namespace PowerTrove.Pack.Data.Tests;

public sealed class ExcelActionContract : ActionContractTestsBase, IDisposable
{
    private readonly TempDirectory _dir = new();
    protected override IAction CreateAction() => new ExcelAction(FileActionServices.CreatePhysical(new ExtensionOnlyTypeVerifier()), "csv-to-xlsx");
    protected override ContextSnapshot CreateValidSnapshot() => Snapshots.ForPaths(_dir.Write("a.csv", "head\r\nvalue\r\n"));
    public void Dispose() => _dir.Dispose();
}
