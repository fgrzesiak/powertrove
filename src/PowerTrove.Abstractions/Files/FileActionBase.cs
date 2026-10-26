using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PowerTrove.Abstractions.Files;

/// <summary>A verified input file, ready for processing.</summary>
public sealed record FileWorkItem(
    SelectedFile File,
    string Path,
    string Directory,
    string BaseName,
    string? DetectedFormat,
    int Index,
    int Count);

/// <summary>An output an action intends to create, used for previews.</summary>
public sealed record PlannedOutput(string BaseName, string Extension, bool IsDirectory = false);

/// <summary>Static extension matching metadata, independent of an action's option type.</summary>
public interface IFileActionMetadata
{
    IReadOnlyCollection<string>? InputExtensions { get; }
}

/// <summary>
/// Shared lifecycle for actions that read selected files and create new outputs.
/// It implements the product rules once, so individual actions only describe
/// their options and the transformation itself:
/// <list type="bullet">
/// <item>static, content-free matching by extension; content verified by signature before processing;</item>
/// <item>stale-selection and option validation with a blocked preview instead of a failing run;</item>
/// <item>outputs next to the source, collision-safe, written transactionally (never overwriting);</item>
/// <item>per-item results, partial success and cancellation that reports completed outputs;</item>
/// <item>actions can throw <see cref="FileSkippedException"/> to indicate an item needed no changes (counted as skipped, not failed);</item>
/// <item>user-facing messages without file names or content.</item>
/// </list>
/// </summary>
public abstract class FileActionBase<TOptions> : IAction, IFileActionMetadata
{
    private const int MaxVerificationsInPreview = 500;

    protected FileActionBase(FileActionServices services)
    {
        Services = services ?? throw new ArgumentNullException(nameof(services));
    }

    protected FileActionServices Services { get; }

    protected static ILookup<string, FileWorkItem> ByFamily(IReadOnlyList<FileWorkItem> items) =>
        items.ToLookup(i => SelectionFamilies.Of(i.File), StringComparer.OrdinalIgnoreCase);

    public abstract ActionDescriptor Descriptor { get; }

    /// <summary>Which selected items the action accepts.</summary>
    protected abstract FileInputFilter Input { get; }

    /// <summary>Static matching metadata, copied so callers cannot mutate the action's filter.</summary>
    public IReadOnlyCollection<string>? InputExtensions => Input.Extensions is null ? null : Array.AsReadOnly(Input.Extensions.ToArray());

    protected virtual int MinimumInputs => 1;

    public int MinimumInputCount => MinimumInputs;

    protected virtual int MaximumInputs => 1000;
    protected virtual bool RequiresEntireSelection => false;
    protected virtual string? ValidateSelection(IReadOnlyList<SelectedFile> inputs) => null;

    /// <summary>Non-null when a required engine is missing on this system.</summary>
    protected virtual string? UnavailableReason => null;

    protected abstract TOptions ReadOptions(ActionParameterValues values);

    /// <summary>Returns a user-facing error when the options cannot be used.</summary>
    protected virtual string? ValidateOptions(TOptions options) => null;

    /// <summary>One sentence describing what will happen, without file names.</summary>
    protected abstract string DescribeOperation(int inputCount, TOptions options);

    protected virtual IEnumerable<string> GetWarnings(IReadOnlyList<SelectedFile> inputs, TOptions options) => Array.Empty<string>();

    /// <summary>Orders accepted inputs. Default: natural file-name order (stable and visible to users).</summary>
    protected virtual IReadOnlyList<SelectedFile> OrderInputs(IEnumerable<SelectedFile> files) => NaturalFileNameComparer.Sort(files);

    protected abstract IReadOnlyList<string> PlanExpectedOutputs(IReadOnlyList<SelectedFile> inputs, TOptions options);

    protected abstract Task RunAsync(
        IReadOnlyList<SelectedFile> inputs,
        TOptions options,
        FileOutputWriter output,
        List<ActionFailure> failures,
        ExecutionStatistics statistics,
        IProgress<ActionProgress>? progress,
        CancellationToken cancellationToken);

    /// <summary>Optional success summary, for example size savings.</summary>
    protected virtual string? DescribeResult(IReadOnlyList<string> outputs, ExecutionStatistics statistics) => statistics.ResultMessage;

    /// <summary>Per-execution counters (actions are shared instances and must stay stateless).</summary>
    protected sealed class ExecutionStatistics
    {
        public long InputBytes { get; set; }
        public long OutputBytes { get; set; }
        public int Skipped { get; set; }
        public string? ResultMessage { get; set; }
        public bool CompletedWithoutOutput { get; set; }
        public Guid? UndoOperationId { get; set; }
        public DateTimeOffset? UndoUntil { get; set; }
    }

    public ValueTask<ActionAvailability> MatchAsync(ContextSnapshot context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var compatibility = ActionContextCompatibility.Check(context, Descriptor);
        if (!compatibility.IsAvailable)
            return new ValueTask<ActionAvailability>(compatibility);
        if (context.Content is not FileSelection selection)
            return Result(false, "Not a file selection.");

        if (UnavailableReason is string reason)
            return Result(false, reason);

        if (RequiresEntireSelection && selection.Files.Any(f => !Input.Accepts(f)))
            return Result(false, Resources.SdkStrings.Error_ContentNotSupported(Input.DisplayName));
        if (ValidateSelection(selection.Files.Where(Input.Accepts).ToArray()) is string selectionProblem)
            return Result(false, selectionProblem);
        int accepted = selection.Files.Count(Input.Accepts);
        if (accepted == 0)
            return Result(false, $"No supported {Input.DisplayName} in the selection.");
        if (accepted < MinimumInputs)
            return Result(false, $"Select at least {MinimumInputs} {Input.DisplayName}.");
        if (accepted > MaximumInputs)
            return Result(false, $"Select at most {MaximumInputs} {Input.DisplayName}.");
        if (accepted < selection.Files.Count)
            return Result(true, Resources.SdkStrings.AppliesToSome(accepted, selection.Files.Count));

        return Result(true, null);

        static ValueTask<ActionAvailability> Result(bool available, string? why) =>
            new(new ActionAvailability(available, why));
    }

    public virtual async ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters parameters, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (TryGetBlockingProblem(context, parameters, out var inputs, out var options, out var problem))
            return Blocked(problem!);

        var warnings = new List<string>();
        int skipped = ((FileSelection)context.Content).Files.Count - inputs.Count;
        if (skipped > 0)
            warnings.Add(Resources.SdkStrings.AppliesToSome(inputs.Count, ((FileSelection)context.Content).Files.Count));

        var verified = new List<SelectedFile>();
        int rejected = 0;
        foreach (var file in inputs.Take(MaxVerificationsInPreview))
        {
            var check = await VerifyAsync(file, cancellationToken).ConfigureAwait(false);
            if (check.IsAccepted) verified.Add(file); else rejected++;
        }
        verified.AddRange(inputs.Skip(MaxVerificationsInPreview));

        if (rejected > 0)
            warnings.Add($"{rejected} file(s) do not contain valid {Input.DisplayName} content and will be skipped.");

        if (verified.Count < MinimumInputs)
            return Blocked($"Select at least {MinimumInputs} valid {Input.DisplayName}.", warnings);

        warnings.AddRange(GetWarnings(verified, options!));

        IReadOnlyList<string> outputs;
        try
        {
            outputs = PlanExpectedOutputs(verified, options!);
        }
        catch (FileProcessingException ex)
        {
            return Blocked(ex.Message, warnings);
        }

        string text = DescribeOperation(verified.Count, options!) +
            (Descriptor.ChangesFiles ? " " + Resources.SdkStrings.Action_ChangesFiles : string.Empty);

        return new ActionPreview(
            RequiresConfirmation: Descriptor.RequiresPreview || Descriptor.ChangesFiles,
            PreviewText: text,
            ExpectedOutputs: outputs,
            Warnings: warnings);
    }

    public async Task<ActionReceipt> ExecuteAsync(ContextSnapshot context, ActionParameters parameters, IProgress<ActionProgress>? progress, CancellationToken cancellationToken)
    {
        // A request that is already canceled reports cancellation, never a validation failure.
        if (cancellationToken.IsCancellationRequested)
            return new ActionReceipt(false, true, Array.Empty<string>(), Array.Empty<ActionFailure>(), false, Resources.SdkStrings.Summary_Canceled);
        if (TryGetBlockingProblem(context, parameters, out var inputs, out var options, out var problem))
            return new ActionReceipt(false, false, Array.Empty<string>(), new[] { new ActionFailure(null, problem!) }, false, problem);

        int skipped = ((FileSelection)context.Content).Files.Count - inputs.Count;
        var output = new FileOutputWriter(Services.FileSystem, Services.Planner, Services.OutputDirectoryOverride.Value);
        var failures = new List<ActionFailure>();
        var statistics = new ExecutionStatistics();
        bool canceled = false;

        progress?.Report(new ActionProgress(0, null, Resources.SdkStrings.Progress_Starting));
        try
        {
            await RunAsync(inputs, options!, output, failures, statistics, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            canceled = true;
        }

        var outputs = output.Outputs.ToList();
        bool success = !canceled && failures.Count == 0 && (outputs.Count + statistics.Skipped > 0 || statistics.CompletedWithoutOutput);
        string message = BuildMessage(outputs, statistics, failures.Count, skipped, canceled, success);
        if (!canceled) progress?.Report(new ActionProgress(1, null, Resources.SdkStrings.Progress_Done));

        return new ActionReceipt(success, canceled, outputs, failures, IsUndoable: statistics.UndoOperationId.HasValue, Message: message,
            UndoOperationId: statistics.UndoOperationId, UndoUntil: statistics.UndoUntil);
    }

    /// <summary>Re-validates a file at execution time and verifies its content type.</summary>
    protected async Task<(FileWorkItem? Item, ActionFailure? Failure)> PrepareAsync(SelectedFile file, int index, int count, CancellationToken cancellationToken)
    {
        string path = file.Path!;
        if (!file.IsDirectory && !Services.FileSystem.FileExists(path))
            return (null, new ActionFailure(path, Resources.SdkStrings.Error_FileNoLongerExists));
        if (file.IsDirectory && !Services.FileSystem.DirectoryExists(path))
            return (null, new ActionFailure(path, Resources.SdkStrings.Error_FolderNoLongerExists));

        var check = await VerifyAsync(file, cancellationToken).ConfigureAwait(false);
        if (!check.IsAccepted)
            return (null, new ActionFailure(path, check.Reason ?? Resources.SdkStrings.Error_ContentNotSupported(Input.DisplayName)));

        string directory = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path)) ?? string.Empty;
        string baseName = file.IsDirectory
            ? Path.GetFileName(Path.TrimEndingDirectorySeparator(path))
            : Path.GetFileNameWithoutExtension(path);
        return (new FileWorkItem(file, path, directory, baseName, check.DetectedFormat, index, count), null);
    }

    /// <summary>Maps an exception to a user-facing message that contains no paths or content.</summary>
    protected static string Plural(int count, string singular, string plural) => count == 1 ? $"1 {singular}" : $"{count} {plural}";

    protected static string DescribeException(Exception exception) => exception switch
    {
        FileProcessingException => exception.Message,
        UnauthorizedAccessException => Resources.SdkStrings.Error_AccessDenied,
        FileNotFoundException => Resources.SdkStrings.Error_FileNotFound,
        DirectoryNotFoundException => Resources.SdkStrings.Error_FolderNotFound,
        PathTooLongException => Resources.SdkStrings.Error_PathTooLong,
        IOException => Resources.SdkStrings.Error_FileSystemError,
        NotSupportedException => string.IsNullOrWhiteSpace(exception.Message) ? Resources.SdkStrings.Error_OperationNotSupported : exception.Message,
        _ => Resources.SdkStrings.Error_ProcessingFailed
    };



    private async ValueTask<FileTypeVerification> VerifyAsync(SelectedFile file, CancellationToken cancellationToken)
    {
        if (!Input.RequiresContentVerification(file))
            return FileTypeVerification.Accepted(null);

        try
        {
            return await Services.TypeVerifier.VerifyAsync(file.Path!, Input, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return FileTypeVerification.Rejected(null, DescribeException(ex));
        }
    }

    private bool TryGetBlockingProblem(
        ContextSnapshot context,
        ActionParameters parameters,
        out IReadOnlyList<SelectedFile> inputs,
        out TOptions? options,
        out string? problem)
    {
        inputs = Array.Empty<SelectedFile>();
        options = default;
        problem = null;

        if (context.IsStale)
            problem = Resources.SdkStrings.Error_SelectionExpired;
        else if (context.Content is not FileSelection selection)
            problem = Resources.SdkStrings.Error_NotAFileSelection;
        else if (UnavailableReason is string reason)
            problem = reason;
        else if (ActionContextCompatibility.Check(context, Descriptor) is { IsAvailable: false } compatibility)
            problem = compatibility.Reason;
        else
        {
            inputs = OrderInputs(selection.Files.Where(Input.Accepts));
            if (RequiresEntireSelection && inputs.Count != selection.Files.Count)
                problem = Resources.SdkStrings.Error_ContentNotSupported(Input.DisplayName);
            else if (ValidateSelection(inputs) is string selectionProblem)
                problem = selectionProblem;
            else if (inputs.Count == 0)
                problem = Resources.SdkStrings.Error_NoSupportedFiles(Input.DisplayName);
            else if (inputs.Count < MinimumInputs)
                problem = Resources.SdkStrings.Error_SelectAtLeast(MinimumInputs, Input.DisplayName);
            else if (inputs.Count > MaximumInputs)
                problem = Resources.SdkStrings.Error_SelectAtMost(MaximumInputs, Input.DisplayName);
            else
            {
                var values = ActionParameterValues.From(parameters, Descriptor.Parameters);
                if (Descriptor.Parameters != null)
                {
                    foreach (var p in Descriptor.Parameters)
                    {
                        if (p.VisibleWhenParameter != null)
                        {
                            if (!values.TryRaw(p.VisibleWhenParameter, out var conditionValue))
                            {
                                // If raw value not found, check the default value of the dependent parameter
                                var dependentParam = Descriptor.Parameters.FirstOrDefault(dp => dp.Name == p.VisibleWhenParameter);
                                conditionValue = dependentParam?.DefaultValue ?? string.Empty;
                            }
                            if (!string.Equals(conditionValue, p.VisibleWhenValue, StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }
                        }

                        if (p.Kind == ActionParameterKind.FilePath)
                        {
                            string path = values.GetFilePath(p.Name);
                            if (p.MustExist == true && !Services.FileSystem.FileExists(path))
                            {
                                problem = p.FileTypes != null && p.FileTypes.Count > 0
                                    ? Resources.SdkStrings.Error_ChooseExistingFileTypes(string.Join(" or ", p.FileTypes.Select(ext => ext.TrimStart('.').ToUpperInvariant())))
                                    : Resources.SdkStrings.Error_ChooseExistingFile;
                                break;
                            }
                            if (p.FileTypes != null && p.FileTypes.Count > 0 && path.Length > 0)
                            {
                                string ext = Path.GetExtension(path);
                                if (!p.FileTypes.Any(t => string.Equals(ext, t, StringComparison.OrdinalIgnoreCase)))
                                {
                                    problem = Resources.SdkStrings.Error_ChooseExistingFileTypes(string.Join(" or ", p.FileTypes.Select(t => t.TrimStart('.').ToUpperInvariant())));
                                    break;
                                }
                            }
                        }
                    }
                }

                if (problem == null)
                {
                    options = ReadOptions(values);
                    problem = ValidateOptions(options);
                }
            }
        }

        return problem is not null;
    }

    private ActionPreview Blocked(string problem, IReadOnlyList<string>? warnings = null) =>
        new(false, problem, Array.Empty<string>(), warnings ?? Array.Empty<string>(), IsBlocked: true);

    private string BuildMessage(IReadOnlyList<string> outputs, ExecutionStatistics statistics, int failed, int skipped, bool canceled, bool success)
    {
        var parts = new List<string>();
        if (canceled)
            parts.Add(Resources.SdkStrings.Summary_CanceledOutputs(outputs.Count));
        else if (success)
            parts.Add(DescribeResult(outputs, statistics) ?? Resources.SdkStrings.Summary_Created(outputs.Count));
        else if (outputs.Count > 0)
            parts.Add(Resources.SdkStrings.Summary_CreatedFailed(outputs.Count, failed));
        else
            parts.Add(Resources.SdkStrings.Summary_NoOutput);

        if (skipped > 0)
            parts.Add(Resources.SdkStrings.Summary_Skipped(skipped));
        if (statistics.Skipped > 0)
            parts.Add(Resources.SdkStrings.Summary_NeededNoChanges(statistics.Skipped));
        return string.Join(" ", parts);
    }
}

/// <summary>Actions that create one or more outputs for each input file (convert, resize, split...).</summary>
public abstract class PerFileAction<TOptions> : FileActionBase<TOptions>
{
    protected PerFileAction(FileActionServices services) : base(services) { }

    /// <summary>Outputs this input will produce, for the preview.</summary>
    protected abstract IEnumerable<PlannedOutput> PlanOutputs(SelectedFile input, TOptions options);

    protected abstract Task ProcessAsync(FileWorkItem item, TOptions options, FileOutputWriter output, CancellationToken cancellationToken);

    /// <summary>Verb for progress messages, e.g. "Converting".</summary>
    protected virtual string ProgressVerb => Resources.SdkStrings.Progress_Processing;

    /// <summary>When true, the success message reports the total size change.</summary>
    protected virtual bool ReportSizeChange => false;

    protected override IReadOnlyList<string> PlanExpectedOutputs(IReadOnlyList<SelectedFile> inputs, TOptions options)
    {
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var input in inputs)
        {
            string directory = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(input.Path!)) ?? string.Empty;
            if (directory.Length == 0) continue;
            foreach (var planned in PlanOutputs(input, options))
            {
                var plan = planned.IsDirectory
                    ? Services.Planner.PlanDirectory(directory, planned.BaseName, reserved)
                    : Services.Planner.PlanFile(directory, planned.BaseName, planned.Extension, reserved);
                reserved.Add(plan.TargetPath);
                result.Add(plan.TargetPath);
            }
        }
        return result;
    }

    protected override async Task RunAsync(
        IReadOnlyList<SelectedFile> inputs,
        TOptions options,
        FileOutputWriter output,
        List<ActionFailure> failures,
        ExecutionStatistics statistics,
        IProgress<ActionProgress>? progress,
        CancellationToken cancellationToken)
    {
        for (int i = 0; i < inputs.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new ActionProgress((double)i / inputs.Count, null, Resources.SdkStrings.Progress_Item(ProgressVerb, i + 1, inputs.Count)));

            var (item, failure) = await PrepareAsync(inputs[i], i, inputs.Count, cancellationToken).ConfigureAwait(false);
            if (item is null)
            {
                failures.Add(failure!);
                continue;
            }

            int before = output.Outputs.Count;
            try
            {
                await ProcessAsync(item, options, output, cancellationToken).ConfigureAwait(false);
                if (ReportSizeChange) TrackSizes(item, output, before, statistics);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (FileSkippedException)
            {
                statistics.Skipped++;
            }
            catch (Exception ex)
            {
                failures.Add(new ActionFailure(item.Path, DescribeException(ex)));
            }
        }
    }

    protected override string? DescribeResult(IReadOnlyList<string> outputs, ExecutionStatistics statistics)
    {
        string created = Resources.SdkStrings.Summary_Created(outputs.Count);
        long input = statistics.InputBytes, produced = statistics.OutputBytes;
        if (!ReportSizeChange || input <= 0 || produced <= 0)
            return created;

        long diff = input - produced;
        return diff >= 0
            ? Resources.SdkStrings.Summary_Saved(outputs.Count, FormatBytes(diff), FormatBytes(input), FormatBytes(produced))
            : Resources.SdkStrings.Summary_Larger(outputs.Count, FormatBytes(-diff), FormatBytes(input), FormatBytes(produced));
    }

    private void TrackSizes(FileWorkItem item, FileOutputWriter output, int firstNewOutput, ExecutionStatistics statistics)
    {
        try
        {
            long produced = 0;
            for (int o = firstNewOutput; o < output.Outputs.Count; o++)
            {
                if (Services.FileSystem.FileExists(output.Outputs[o]))
                    produced += Services.FileSystem.GetFileLength(output.Outputs[o]);
            }
            statistics.InputBytes += Services.FileSystem.GetFileLength(item.Path);
            statistics.OutputBytes += produced;
        }
        catch (IOException)
        {
            // Size reporting is informational only.
        }
    }

    public static string FormatBytes(long bytes)
    {
        string[] suffixes = { Resources.SdkStrings.Unit_B, Resources.SdkStrings.Unit_KB, Resources.SdkStrings.Unit_MB, Resources.SdkStrings.Unit_GB, Resources.SdkStrings.Unit_TB };
        int i = 0;
        double value = bytes;
        while (i < suffixes.Length - 1 && value >= 1024)
        {
            value /= 1024;
            i++;
        }
        return string.Create(System.Globalization.CultureInfo.CurrentCulture, $"{value:0.##} {suffixes[i]}");
    }
}

/// <summary>Actions that combine several inputs into a single output (merge, archive...).</summary>
public abstract class CombineFilesAction<TOptions> : FileActionBase<TOptions>
{
    protected CombineFilesAction(FileActionServices services) : base(services) { }

    protected override int MinimumInputs => 2;

    /// <summary>The single output, planned next to the first input (in processing order).</summary>
    protected abstract PlannedOutput PlanOutput(IReadOnlyList<SelectedFile> inputs, TOptions options);

    protected abstract Task ProcessAsync(
        IReadOnlyList<FileWorkItem> items,
        TOptions options,
        FileOutputWriter output,
        IProgress<double>? progress,
        CancellationToken cancellationToken);

    /// <summary>Optional per-execution result metadata without mutable shared action state.</summary>
    protected virtual Task ProcessWithStatisticsAsync(IReadOnlyList<FileWorkItem> items, TOptions options,
        FileOutputWriter output, ExecutionStatistics statistics, IProgress<double>? progress, CancellationToken cancellationToken) =>
        ProcessAsync(items, options, output, progress, cancellationToken);

    protected override IReadOnlyList<string> PlanExpectedOutputs(IReadOnlyList<SelectedFile> inputs, TOptions options)
    {
        string directory = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(inputs[0].Path!)) ?? string.Empty;
        if (directory.Length == 0) return Array.Empty<string>();
        var planned = PlanOutput(inputs, options);
        var plan = planned.IsDirectory
            ? Services.Planner.PlanDirectory(directory, planned.BaseName)
            : Services.Planner.PlanFile(directory, planned.BaseName, planned.Extension);
        return new[] { plan.TargetPath };
    }

    protected override async Task RunAsync(
        IReadOnlyList<SelectedFile> inputs,
        TOptions options,
        FileOutputWriter output,
        List<ActionFailure> failures,
        ExecutionStatistics statistics,
        IProgress<ActionProgress>? progress,
        CancellationToken cancellationToken)
    {
        var items = new List<FileWorkItem>();
        for (int i = 0; i < inputs.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (item, failure) = await PrepareAsync(inputs[i], i, inputs.Count, cancellationToken).ConfigureAwait(false);
            if (item is null) failures.Add(failure!);
            else items.Add(item);
        }

        if (items.Count < MinimumInputs)
        {
            failures.Add(new ActionFailure(null, Resources.SdkStrings.Error_AtLeastValidRequired(MinimumInputs, Input.DisplayName)));
            return;
        }

        // Partial input sets are not combined silently: a merge with a missing
        // file would look complete but is not what the user previewed.
        if (failures.Count > 0)
            return;

        var stepProgress = progress is null
            ? null
            : new Progress<double>(p => progress.Report(new ActionProgress(Math.Clamp(p, 0, 1), null, Resources.SdkStrings.Progress_Working)));
        try
        {
            await ProcessWithStatisticsAsync(items, options, output, statistics, stepProgress, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (FileSkippedException)
        {
            statistics.Skipped += items.Count;
        }
        catch (Exception ex)
        {
            failures.Add(new ActionFailure(null, DescribeException(ex)));
        }
    }
}
