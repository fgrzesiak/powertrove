using System.Security.Cryptography;
using System.Text;

namespace ContextBar.Pack.Files;

public sealed class RenameFilesAction(FileActionServices services, IFileRenameService? renamer) : CombineFilesAction<RenameOptions>(services)
{
    public const string ApprovalParameter = "RenameApproval";
    protected override string? UnavailableReason => renamer is null ? SR.GetString("RenameUnavailable") : null;
    public override ActionDescriptor Descriptor => new("contextbar.files.rename", SR.GetString("RenameFiles"), SR.GetString("RenameDescription"),
        new HashSet<ContentKind> { ContentKind.Files }, ["rename", "names", "number", "batch"], true, true,
        Parameters: [ActionParameterDefinition.Text("Pattern", SR.GetString("RenamePattern"), "{name}", SR.GetString("RenameTokens")),
            ActionParameterDefinition.Text("Find", SR.GetString("RenameFind"), ""), ActionParameterDefinition.Text("Replace", SR.GetString("RenameReplace"), ""),
            FileTools.Choice("Case", "keep", "keep", "lower", "upper", "title"),
            ActionParameterDefinition.Integer("StartAt", SR.GetString("RenameStartAt"), 1, 0, 1000000000),
            ActionParameterDefinition.Boolean("KeepExtension", SR.GetString("RenameKeepExtension"), true)],
        Selection: SelectionRequirements.Any(), CanRememberParameters: false);
    protected override FileInputFilter Input => FileTools.Any();
    protected override int MinimumInputs => 1;
    protected override bool RequiresEntireSelection => true;
    protected override RenameOptions ReadOptions(ActionParameterValues v) => new(Raw(v, "Pattern", "{name}"), Raw(v, "Find", ""), Raw(v, "Replace", ""), v.GetChoice("Case"), v.GetInt32("StartAt"), v.GetBoolean("KeepExtension"), v.TryRaw(ApprovalParameter, out var a) ? a : "");
    private static string Raw(ActionParameterValues v, string key, string fallback) => v.TryRaw(key, out var value) ? value : fallback;
    protected override string DescribeOperation(int count, RenameOptions options) => SR.Format("RenameOperation", count);
    protected override PlannedOutput PlanOutput(IReadOnlyList<SelectedFile> inputs, RenameOptions options) => throw new NotSupportedException();
    protected override IReadOnlyList<string> PlanExpectedOutputs(IReadOnlyList<SelectedFile> inputs, RenameOptions options) => [];
    public override async ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters parameters, CancellationToken ct)
    {
        var preview = await base.PreviewAsync(context, parameters, ct).ConfigureAwait(false);
        if (preview.IsBlocked) return preview;
        try
        {
            var options = ReadOptions(ActionParameterValues.From(parameters, Descriptor.Parameters));
            var inputs = OrderInputs(((FileSelection)context.Content).Files);
            var plan = await Task.Run(() => BuildPlan(inputs, options, ct), ct).ConfigureAwait(false);
            if (!FileRenameRules.IsValid(plan.Renames, Services.FileSystem)) return preview with { IsBlocked = true, PreviewText = SR.GetString("RenameInvalid") };
            if (plan.Renames.All(r => r.OriginalPath == r.TargetPath)) return preview with { IsBlocked = true, PreviewText = SR.GetString("RenameUnchanged") };
            return preview with
            {
                PreviewText = DescribeOperation(inputs.Count, options),
                RenameRows = plan.Renames.Select(r => new RenamePreviewRow(Path.GetFileName(r.OriginalPath), Path.GetFileName(r.TargetPath))).ToArray(),
                Confirmation = new(SR.GetString("RenameConsent"), ApprovalParameter, plan.Token), Warnings = [SR.GetString("RenameUndoWindow")]
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OverflowException)
        { return preview with { IsBlocked = true, PreviewText = SR.GetString("RenameInvalid") }; }
    }
    private (IReadOnlyList<FileRename> Renames, string Token) BuildPlan(IReadOnlyList<SelectedFile> files, RenameOptions options, CancellationToken ct)
    {
        var renames = new List<FileRename>(); using var stamp = new MemoryStream(); using var writer = new BinaryWriter(stamp, Encoding.UTF8, leaveOpen: true);
        writer.Write(options.Pattern); writer.Write(options.Find); writer.Write(options.Replace); writer.Write(options.Case); writer.Write(options.StartAt); writer.Write(options.KeepExtension);
        for (int i = 0; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested(); string path = Path.GetFullPath(files[i].Path!); var info = new FileInfo(path);
            if (!info.Exists || FileTools.IsLink(path)) throw new IOException();
            DateTime modified = info.LastWriteTimeUtc; DateTime? photo = null;
            if (options.Pattern.Contains("{exifdate}", StringComparison.Ordinal))
            { using var source = Services.FileSystem.OpenRead(path); photo = ExifDateReader.Read(source, ct); }
            string name = RenameNames.Generate(info.Name, options, i, modified.ToLocalTime(), photo);
            if (!FileRenameRules.IsValidName(name)) throw new ArgumentException("Invalid name.");
            string target = Path.Combine(info.DirectoryName!, name);
            renames.Add(new(path, target)); writer.Write(path); writer.Write(target); writer.Write(info.Length); writer.Write(modified.Ticks);
        }
        writer.Flush(); return (renames, Convert.ToHexString(SHA256.HashData(stamp.ToArray())));
    }
    protected override Task ProcessAsync(IReadOnlyList<FileWorkItem> items, RenameOptions options, FileOutputWriter output, IProgress<double>? progress, CancellationToken ct) => throw new NotSupportedException();
    protected override async Task ProcessWithStatisticsAsync(IReadOnlyList<FileWorkItem> items, RenameOptions options, FileOutputWriter output, ExecutionStatistics stats, IProgress<double>? progress, CancellationToken ct)
    {
        var plan = await Task.Run(() => BuildPlan(items.Select(i => i.File).ToArray(), options, ct), ct).ConfigureAwait(false);
        if (options.Approval.Length == 0 || options.Approval != plan.Token) throw new FileProcessingException(SR.GetString("RenameReviewAgain"));
        try
        {
            var result = await renamer!.RenameAsync(plan.Renames, ct).ConfigureAwait(false);
            stats.CompletedWithoutOutput = true; stats.UndoOperationId = result.OperationId; stats.UndoUntil = result.UndoUntil;
            stats.ResultMessage = string.Format(SR.GetString(result.RenamedCount == 1 ? "RenameCompleted_One" : "RenameCompleted_Other"), result.RenamedCount);
        }
        catch (RenameRecoveryException ex)
        {
            stats.UndoOperationId = ex.OperationId; stats.UndoUntil = ex.UndoUntil;
            throw new FileProcessingException(SR.GetString("RenameRecovery"), ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new FileProcessingException(SR.GetString("RenameFailed"), ex); }
    }
}
