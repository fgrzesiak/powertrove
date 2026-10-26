namespace ContextBar.Pack.Tables;

/// <summary>Exports of table selections (for example an Excel range) to text formats.</summary>
public sealed class TablesPack : IActionPack
{
    public TablesPack(IOutputPlanner planner, IOutputFileSystem fileSystem, FileActionServices? services = null)
    {
        ArgumentNullException.ThrowIfNull(planner);
        ArgumentNullException.ThrowIfNull(fileSystem);
        Actions = new IAction[]
        {
            new ExportCsvAction(planner, fileSystem),
            new ExportJsonAction(planner, fileSystem),
            new ExportMarkdownAction(planner, fileSystem),
            new ExportSqlInsertAction(planner, fileSystem)
        };
        if (services is not null) Actions = Actions.Concat(new IAction[] { new CombineWorkbookAction(services) }).ToArray();
    }

    public ActionPackInfo Info { get; } = Describe();

    internal static ActionPackInfo Describe() => new("contextbar.tables", SR.GetString("TablesPack_Title"), "0.1.0", "PowerTrove");

    public IReadOnlyList<IAction> Actions { get; }
}
