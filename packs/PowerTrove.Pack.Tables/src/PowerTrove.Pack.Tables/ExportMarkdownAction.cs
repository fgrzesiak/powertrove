using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PowerTrove.Abstractions.Files;

namespace PowerTrove.Pack.Tables;

public sealed class ExportMarkdownAction : IAction
{
    private readonly IOutputPlanner _planner;
    private readonly IOutputFileSystem _fileSystem;

    public ExportMarkdownAction(IOutputPlanner planner, IOutputFileSystem fileSystem)
    {
        _planner = planner ?? throw new ArgumentNullException(nameof(planner));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    }

    public ActionDescriptor Descriptor { get; } = new(
        Id: "contextbar.table.export-markdown",
        Title: SR.GetString("ExportMarkdownAction_Title"),
        Description: SR.GetString("ExportMarkdownAction_Description"),
        Accepts: new HashSet<ContentKind> { ContentKind.Table },
        SearchTerms: new[] { "markdown", "md", "export", "table" },
        RequiresPreview: true,
        ChangesFiles: true);

    public ValueTask<ActionAvailability> MatchAsync(
        ContextSnapshot context,
        CancellationToken cancellationToken)
    {
        if (context.Content is TableData tableData)
        {
            if (tableData.Rows.Count == 0)
            {
                return new(new ActionAvailability(false, SR.GetString("TableEmpty")));
            }
            return new(new ActionAvailability(true));
        }
        return new(new ActionAvailability(false, SR.GetString("TableSelectionRequired")));
    }

    private string FormatCellValue(CellValue cell) => FileKit.Tabular.CsvWriter.FormatCellValue(TableMapping.Map(cell));

    private string EscapeMarkdownField(string field) =>
        FileKit.Tabular.MarkdownTableWriter.EscapeMarkdownField(field);

    public ValueTask<ActionPreview> PreviewAsync(
        ContextSnapshot context,
        ActionParameters parameters,
        CancellationToken cancellationToken)
    {
        try
        {
            if (context.Content is not TableData table)
            {
                return new(new ActionPreview(false, SR.GetString("TableRequired"), Array.Empty<string>(), Array.Empty<string>()));
            }

            var previewRows = table.Rows.Take(5).ToList();
            var sb = new StringBuilder();

            for (int i = 0; i < previewRows.Count; i++)
            {
                var row = previewRows[i];
                var fields = row.Select(FormatCellValue).Select(EscapeMarkdownField);
                sb.AppendLine("| " + string.Join(" | ", fields) + " |");

                if (i == 0)
                {
                    // Add separator after first row
                    var separators = row.Select(_ => "---");
                    sb.AppendLine("| " + string.Join(" | ", separators) + " |");
                }
            }

            if (table.Rows.Count > 5)
            {
                sb.AppendLine(SR.GetString("TablePreviewTruncated"));
            }

            string defaultTargetDir = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            if (string.IsNullOrWhiteSpace(defaultTargetDir))
            {
                defaultTargetDir = Directory.GetCurrentDirectory();
            }
            var plan = _planner.PlanOutput("Export.md", defaultTargetDir, ".md", overwrite: false);

            return new(new ActionPreview(
                RequiresConfirmation: true,
                PreviewText: sb.ToString().TrimEnd(),
                ExpectedOutputs: new[] { plan.TargetPath },
                Warnings: Array.Empty<string>()));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return new(new ActionPreview(false, TableMessages.Describe(ex), Array.Empty<string>(), Array.Empty<string>(), IsBlocked: true));
        }
    }

    private System.IO.Stream OpenNewOutput(string path, ref bool created)
    {
        var stream = _fileSystem.CreateFile(path);
        created = true;
        return stream;
    }

    public Task<ActionReceipt> ExecuteAsync(
        ContextSnapshot context,
        ActionParameters parameters,
        IProgress<ActionProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (context.IsStale)
        {
            return Task.FromResult(new ActionReceipt(false, false, Array.Empty<string>(), new[] { new ActionFailure(null, SR.GetString("TableContextExpired")) }, false));
        }

        if (context.Content is not TableData table)
        {
            return Task.FromResult(new ActionReceipt(false, false, Array.Empty<string>(), new[] { new ActionFailure(null, SR.GetString("TableRequired")) }, false));
        }

        // Use user's desktop as default output dir if parameters didn't specify one
        string outputDir = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        if (string.IsNullOrWhiteSpace(outputDir))
        {
            outputDir = Directory.GetCurrentDirectory();
        }
        if (parameters is DictionaryParameters dict && dict.Values.TryGetValue("OutputDirectory", out var paramDir) && !string.IsNullOrWhiteSpace(paramDir))
        {
            outputDir = paramDir;
        }

        string baseFileName = "Export.md";
        if (parameters is DictionaryParameters dictName && dictName.Values.TryGetValue("BaseFileName", out var paramName) && !string.IsNullOrWhiteSpace(paramName))
        {
            baseFileName = paramName;
        }

        OutputPlan? plan = null;
        bool outputCreated = false;
        try
        {
            plan = _planner.PlanOutput(baseFileName, outputDir, ".md", overwrite: false);
            _planner.PrepareOutputFolder(plan);

            // Markdown usually defaults to UTF-8 without BOM
            Encoding encoding = new UTF8Encoding(false);

            using (var stream = OpenNewOutput(plan.TargetPath, ref outputCreated))
            using (var writer = new StreamWriter(stream, encoding))
            {
                FileKit.Tabular.MarkdownTableWriter.Write(writer, TableMapping.Map(table), cancellationToken);
                progress?.Report(new ActionProgress(1, null, null));
            }

            return Task.FromResult(new ActionReceipt(
                IsSuccess: true,
                IsCanceled: false,
                OutputPaths: new[] { plan.TargetPath },
                Failures: Array.Empty<ActionFailure>(),
                IsUndoable: false));
        }
        catch (OperationCanceledException)
        {
             if (outputCreated && plan != null) try { _fileSystem.DeleteFile(plan.TargetPath); } catch { }
             return Task.FromResult(new ActionReceipt(
                IsSuccess: false,
                IsCanceled: true,
                OutputPaths: Array.Empty<string>(),
                Failures: Array.Empty<ActionFailure>(),
                IsUndoable: false));
        }
        catch (Exception ex)
        {
            // Only delete files created by this attempt; never delete a pre-existing
            // file after a CreateNew collision.
            if (outputCreated && plan != null) try { _fileSystem.DeleteFile(plan.TargetPath); } catch { }
             return Task.FromResult(new ActionReceipt(
                IsSuccess: false,
                IsCanceled: false,
                OutputPaths: Array.Empty<string>(),
                Failures: new[] { new ActionFailure(null, TableMessages.Describe(ex)) },
                IsUndoable: false));
        }
    }
}
