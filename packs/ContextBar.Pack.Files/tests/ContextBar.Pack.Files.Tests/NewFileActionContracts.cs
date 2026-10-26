namespace ContextBar.Pack.Files.Tests;

public abstract class NewFileContract : ActionContractTestsBase, IDisposable
{
    protected readonly TempDirectory Directory = new();
    protected static FileActionServices Services() => FileActionServices.CreatePhysical(new ExtensionOnlyTypeVerifier());
    public void Dispose() => Directory.Dispose();
}
public sealed class CompareContract : NewFileContract
{
    protected override IAction CreateAction() => new CompareTextAction(Services());
    protected override ContextSnapshot CreateValidSnapshot() => Snapshots.ForPaths(Directory.Write("a.txt", "a"), Directory.Write("b.txt", "b"));
}
public sealed class SortContract : NewFileContract
{
    protected override IAction CreateAction() => new SortByTypeAction(Services());
    protected override ContextSnapshot CreateValidSnapshot() => Snapshots.ForPaths(Directory.Write("a.txt", "a"), Directory.Write("b.pdf", "%PDF-1.7"));
}
public sealed class AddZipContract : NewFileContract
{
    protected override IAction CreateAction() => new AddToZipAction(Services());
    protected override ContextSnapshot CreateValidSnapshot()
    {
        string zip = Directory.File("input.zip"); using (var archive = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create)) archive.CreateEntry("empty.txt");
        return Snapshots.ForPaths(zip, Directory.Write("a.txt", "a"));
    }
}
public sealed class RenameContract : NewFileContract
{
    protected override IAction CreateAction() => new RenameFilesAction(Services(), null);
    protected override ContextSnapshot CreateValidSnapshot() => Snapshots.ForPaths(Directory.Write("a.txt", "a"));
}
