using System.Text;
using FileKit.Text;

namespace PowerTrove.Pack.Files;

public sealed class CompareTextAction(FileActionServices services) : CombineFilesAction<bool>(services)
{
    public override ActionDescriptor Descriptor => new("powertrove.files.compare-text", SR.GetString("CompareText"), SR.GetString("CompareTextDescription"),
        new HashSet<ContentKind> { ContentKind.Files }, ["compare", "diff", "text"], true, true,
        Parameters: [ActionParameterDefinition.Boolean("IgnoreLineEndings", SR.GetString("IgnoreLineEndings"), true)],
        Selection: new SelectionRequirements(new HashSet<string> { "text" }, MinItems: 2, MaxItems: 2, AllowOtherItems: false,
            FamilyLimits: new Dictionary<string, FamilyLimit>(StringComparer.OrdinalIgnoreCase) { ["text"] = new(2, 2) }));
    protected override int MaximumInputs => 2;
    protected override bool RequiresEntireSelection => true;
    protected override FileInputFilter Input => new(SR.GetString("TextFiles"), [".txt", ".md", ".json", ".xml", ".log", ".ini", ".yaml", ".yml"], null);
    protected override bool ReadOptions(ActionParameterValues values) => values.GetBoolean("IgnoreLineEndings");
    protected override string DescribeOperation(int count, bool options) => SR.GetString("CompareTextDescription");
    protected override PlannedOutput PlanOutput(IReadOnlyList<SelectedFile> inputs, bool options) => new(Stem(inputs[0].Name, inputs[1].Name), ".html");
    private static string Stem(string a, string b) => Path.GetFileNameWithoutExtension(a) + " vs " + Path.GetFileNameWithoutExtension(b) + " (comparison)";
    protected override Task ProcessAsync(IReadOnlyList<FileWorkItem> items, bool options, FileOutputWriter output, IProgress<double>? progress, CancellationToken ct) => throw new NotSupportedException();
    protected override async Task ProcessWithStatisticsAsync(IReadOnlyList<FileWorkItem> items, bool options, FileOutputWriter output, ExecutionStatistics stats, IProgress<double>? progress, CancellationToken ct)
    {
        try
        {
            var texts = new List<string>();
            foreach (var item in items)
            {
                if (Services.FileSystem.GetFileLength(item.Path) > LineDiff.MaxInputBytes) throw new TextLimitException("Input too large.");
                await using var source = Services.FileSystem.OpenRead(item.Path);
                texts.Add(await LineDiff.ReadAsync(source, ct).ConfigureAwait(false));
            }
            var diff = await Task.Run(() => LineDiff.Compare(texts[0], texts[1], options, ct), ct).ConfigureAwait(false);
            await output.WriteFileAsync(items[0].Directory, Stem(items[0].File.Name, items[1].File.Name), ".html", async (stream, token) =>
            {
                await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
                await LineDiff.WriteHtmlAsync(writer, diff, new(SR.GetString("CompareText"), SR.GetString("AddedLines"), SR.GetString("RemovedLines"), SR.GetString("UnchangedLines")), token).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
            stats.ResultMessage = SR.Format("DiffCounts", diff.Added, diff.Removed, diff.Unchanged);
        }
        catch (TextLimitException ex) { throw new FileProcessingException(SR.GetString("ComparisonLimit"), ex); }
        catch (DecoderFallbackException ex) { throw new FileProcessingException(SR.GetString("ComparisonEncoding"), ex); }
    }
}
