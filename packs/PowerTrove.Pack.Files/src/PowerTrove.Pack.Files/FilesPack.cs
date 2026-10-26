namespace PowerTrove.Pack.Files;

/// <summary>General file and folder tools: count, ZIP, unzip, checksums.</summary>
public sealed class FilesPack : IActionPack
{
    public FilesPack(FileActionServices services, IDirectoryScanner? scanner = null, IFileRenameService? renamer = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        Actions = new IAction[]
        {
            new CountFilesAction(scanner ?? new DirectoryScanner()),
            new RenameFilesAction(services, renamer), new AddToZipAction(services), new CompareTextAction(services), new SortByTypeAction(services),
            new CompressToZipAction(services),
            new ExtractZipAction(services),
            new ChecksumAction(services),
            new CompressToTarAction(services), new ExtractTarAction(services),
            new ScanReportAction(services, true, scanner), new ScanReportAction(services, false, scanner),
            new CopySelectionAction(false), new CopySelectionAction(true),
            new SplitFileAction(services), new JoinFileAction(services),
            new Base64Action(services, false), new Base64Action(services, true), new VerifyChecksumsAction(services)
        };
    }

    public ActionPackInfo Info { get; } = Describe();

    internal static ActionPackInfo Describe() => new("powertrove.files", SR.GetString("FilesPack_Title"), "0.1.0", "PowerTrove");

    public IReadOnlyList<IAction> Actions { get; }
}
