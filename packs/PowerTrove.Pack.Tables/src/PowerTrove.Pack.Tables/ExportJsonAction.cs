using System;
using System.Collections.Generic;
using System.Threading;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using PowerTrove.Abstractions.Files;

namespace PowerTrove.Pack.Tables;

public sealed class ExportJsonAction : IAction
{
    private readonly IOutputPlanner _planner;
    private readonly IOutputFileSystem _fileSystem;

    public ExportJsonAction(IOutputPlanner planner, IOutputFileSystem fileSystem)
    {
        _planner = planner ?? throw new ArgumentNullException(nameof(planner));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    }

    public ActionDescriptor Descriptor { get; } = new(
        Id: "contextbar.table.export-json",
        Title: SR.GetString("ExportJsonAction_Title"),
        Description: SR.GetString("ExportJsonAction_Description"),
        Accepts: new HashSet<ContentKind> { ContentKind.Table },
        SearchTerms: new[] { "json", "export", "table" },
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

    private bool ExtractFirstRowAsHeader(ActionParameters parameters)
    {
        if (parameters is DictionaryParameters dict)
        {
            if (dict.Values.TryGetValue("FirstRowAsHeader", out var val) && bool.TryParse(val, out var parsed))
            {
                return parsed;
            }
        }
        return false;
    }

    private string[] ResolveHeaders(IReadOnlyList<CellValue> headerRow) =>
        FileKit.Tabular.JsonTableWriter.ResolveHeaders(TableMapping.Map(headerRow));

    private void WriteCellValue(Utf8JsonWriter writer, CellValue cell) =>
        FileKit.Tabular.JsonTableWriter.WriteCellValue(writer, TableMapping.Map(cell));

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

            bool firstRowAsHeader = ExtractFirstRowAsHeader(parameters);

            using var stream = new MemoryStream();
            using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

            var previewRows = table.Rows.Take(5).ToList();

            writer.WriteStartArray();

            if (firstRowAsHeader && table.Rows.Count > 0)
            {
                var headers = ResolveHeaders(table.Rows[0]);

                for (int i = 1; i < previewRows.Count; i++)
                {
                    writer.WriteStartObject();
                    var row = previewRows[i];
                    for (int j = 0; j < headers.Length; j++)
                    {
                        writer.WritePropertyName(headers[j]);
                        if (j < row.Count)
                        {
                            WriteCellValue(writer, row[j]);
                        }
                        else
                        {
                            writer.WriteNullValue();
                        }
                    }
                    writer.WriteEndObject();
                }
            }
            else
            {
                foreach (var row in previewRows)
                {
                    writer.WriteStartArray();
                    foreach (var cell in row)
                    {
                        WriteCellValue(writer, cell);
                    }
                    writer.WriteEndArray();
                }
            }

            writer.WriteEndArray();
            writer.Flush();

            string previewText = System.Text.Encoding.UTF8.GetString(stream.ToArray());
            if (table.Rows.Count > 5)
            {
                previewText += "\n" + SR.GetString("TablePreviewTruncated");
            }

            string defaultTargetDir = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            if (string.IsNullOrWhiteSpace(defaultTargetDir))
            {
                defaultTargetDir = Directory.GetCurrentDirectory();
            }
            var plan = _planner.PlanOutput("Export.json", defaultTargetDir, ".json", overwrite: false);

            return new(new ActionPreview(
                RequiresConfirmation: true,
                PreviewText: previewText,
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

        bool firstRowAsHeader = ExtractFirstRowAsHeader(parameters);

        string outputDir = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        if (string.IsNullOrWhiteSpace(outputDir))
        {
            outputDir = Directory.GetCurrentDirectory();
        }
        if (parameters is DictionaryParameters dict && dict.Values.TryGetValue("OutputDirectory", out var paramDir) && !string.IsNullOrWhiteSpace(paramDir))
        {
            outputDir = paramDir;
        }

        string baseFileName = "Export.json";
        if (parameters is DictionaryParameters dictName && dictName.Values.TryGetValue("BaseFileName", out var paramName) && !string.IsNullOrWhiteSpace(paramName))
        {
            baseFileName = paramName;
        }

        OutputPlan? plan = null;
        bool outputCreated = false;
        try
        {
            plan = _planner.PlanOutput(baseFileName, outputDir, ".json", overwrite: false);
            _planner.PrepareOutputFolder(plan);

            using (var stream = OpenNewOutput(plan.TargetPath, ref outputCreated))
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                FileKit.Tabular.JsonTableWriter.Write(writer, TableMapping.Map(table, firstRowAsHeader), cancellationToken);
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
