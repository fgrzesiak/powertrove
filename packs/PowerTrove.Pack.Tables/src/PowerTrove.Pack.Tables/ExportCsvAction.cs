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

public sealed class ExportCsvAction : IAction
{
    private readonly IOutputPlanner _planner;
    private readonly IOutputFileSystem _fileSystem;

    public ExportCsvAction(IOutputPlanner planner, IOutputFileSystem fileSystem)
    {
        _planner = planner ?? throw new ArgumentNullException(nameof(planner));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    }

    public ActionDescriptor Descriptor { get; } = new(
        Id: "powertrove.table.export-csv",
        Title: SR.GetString("ExportCsvAction_Title"),
        Description: SR.GetString("ExportCsvAction_Description"),
        Accepts: new HashSet<ContentKind> { ContentKind.Table },
        SearchTerms: new[] { "csv", "export", "table" },
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

    private (string Delimiter, bool IncludeBOM) ExtractOptions(ActionParameters parameters)
    {
        string delimiter = ",";
        bool includeBOM = false;

        if (parameters is DictionaryParameters dict)
        {
            if (dict.Values.TryGetValue("Delimiter", out var d) && !string.IsNullOrEmpty(d))
            {
                delimiter = d;
            }
            if (dict.Values.TryGetValue("IncludeBOM", out var b) && bool.TryParse(b, out var parsedB))
            {
                includeBOM = parsedB;
            }
        }

        return (delimiter, includeBOM);
    }

    private string FormatCellValue(CellValue cell) =>
        FileKit.Tabular.CsvWriter.FormatCellValue(TableMapping.Map(cell));

    private string EscapeCsvField(string field, string delimiter) =>
        FileKit.Tabular.CsvWriter.EscapeCsvField(field, delimiter);

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

            var (delimiter, _) = ExtractOptions(parameters);

            var previewRows = table.Rows.Take(5).ToList();
            var sb = new StringBuilder();
            foreach (var row in previewRows)
            {
                var fields = row.Select(FormatCellValue).Select(f => EscapeCsvField(f, delimiter));
                sb.AppendLine(string.Join(delimiter, fields));
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
            var plan = _planner.PlanOutput("Export.csv", defaultTargetDir, ".csv", overwrite: false);

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

        var (delimiter, includeBOM) = ExtractOptions(parameters);

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

        string baseFileName = "Export.csv";
        if (parameters is DictionaryParameters dictName && dictName.Values.TryGetValue("BaseFileName", out var paramName) && !string.IsNullOrWhiteSpace(paramName))
        {
            baseFileName = paramName;
        }


        OutputPlan? plan = null;
        bool outputCreated = false;
        try
        {
            plan = _planner.PlanOutput(baseFileName, outputDir, ".csv", overwrite: false);
            _planner.PrepareOutputFolder(plan);

            Encoding encoding = includeBOM ? new UTF8Encoding(true) : new UTF8Encoding(false);

            using (var stream = OpenNewOutput(plan.TargetPath, ref outputCreated))
            using (var writer = new StreamWriter(stream, encoding))
            {
                FileKit.Tabular.CsvWriter.Write(writer, TableMapping.Map(table), delimiter, cancellationToken);
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
