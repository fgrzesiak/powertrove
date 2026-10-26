using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PowerTrove.Abstractions.Files;

namespace PowerTrove.Pack.Tables;

public sealed class ExportSqlInsertAction : IAction
{
    private readonly IOutputPlanner _planner;
    private readonly IOutputFileSystem _fileSystem;

    public ExportSqlInsertAction(IOutputPlanner planner, IOutputFileSystem fileSystem)
    {
        _planner = planner ?? throw new ArgumentNullException(nameof(planner));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    }

    public ActionDescriptor Descriptor { get; } = new(
        Id: "contextbar.table.export-sql-insert",
        Title: SR.GetString("ExportSqlInsertAction_Title"),
        Description: SR.GetString("ExportSqlInsertAction_Description"),
        Accepts: new HashSet<ContentKind> { ContentKind.Table },
        SearchTerms: new[] { "sql", "insert", "export", "table", "database" },
        RequiresPreview: true,
        ChangesFiles: true);

    /// <summary>
    /// Formats an identifier (e.g. table name or column name) according to the specified dialect.
    /// PostgreSQL/SQLite use double quotes ("id"), MySQL uses backticks (`id`), and SQLServer uses square brackets ([id]).
    /// </summary>
    private static string NormalizeDialect(string dialect) =>
        FileKit.Tabular.SqlInsertWriter.NormalizeDialect(dialect);

    private static string QuoteIdentifier(string identifier, string dialect) =>
        FileKit.Tabular.SqlInsertWriter.QuoteIdentifier(identifier, dialect);

    /// <summary>
    /// Formats a value for inclusion in a SQL statement, handling NULLs, string escaping, and dialect-specific Unicode prefixes.
    /// EmptyCell and ErrorCell map to NULL.
    /// Single quotes in strings are escaped by doubling them ('').
    /// SQL Server strings may be prefixed with N to denote Unicode strings.
    /// </summary>
    private string FormatValue(CellValue cell, string dialect) =>
        FileKit.Tabular.SqlInsertWriter.FormatValue(TableMapping.Map(cell), dialect);

    private string[] ResolveHeaders(IReadOnlyList<CellValue> headerRow) =>
        FileKit.Tabular.SqlInsertWriter.ResolveHeaders(TableMapping.Map(headerRow));

    private bool ExtractFirstRowAsHeader(ActionParameters parameters)
    {
        if (parameters is DictionaryParameters dict)
        {
            if (dict.Values.TryGetValue("FirstRowAsHeader", out var val) && bool.TryParse(val, out var parsed))
            {
                return parsed;
            }
        }
        return false; // Default to not treating first row as header unless specified
    }

    private string ExtractTableName(ActionParameters parameters)
    {
        if (parameters is DictionaryParameters dict)
        {
            if (dict.Values.TryGetValue("TableName", out var val) && !string.IsNullOrWhiteSpace(val))
            {
                return val;
            }
        }
        return "ExportedData";
    }

    private string ExtractDialect(ActionParameters parameters)
    {
        if (parameters is DictionaryParameters dict)
        {
            if (dict.Values.TryGetValue("Dialect", out var val) && !string.IsNullOrWhiteSpace(val))
            {
                return val;
            }
        }
        return "PostgreSQL";
    }

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

    private string BuildInsertStatement(
        string tableName,
        string dialect,
        IReadOnlyList<CellValue> row,
        string[]? headers = null) =>
        FileKit.Tabular.SqlInsertWriter.BuildInsertStatement(tableName, dialect, TableMapping.Map(row), headers);

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
            string tableName = ExtractTableName(parameters);
            string dialect = ExtractDialect(parameters);

            var previewRows = new List<IReadOnlyList<CellValue>>();
            int maxPreviewRows = 5;

            for (int i = 0; i < Math.Min(maxPreviewRows, table.Rows.Count); i++)
            {
                previewRows.Add(table.Rows[i]);
            }

            string[]? headers = null;
            int startIndex = 0;

            if (firstRowAsHeader && table.Rows.Count > 0)
            {
                headers = ResolveHeaders(table.Rows[0]);
                startIndex = 1;
            }

            var statements = new List<string>();
            for (int i = startIndex; i < previewRows.Count; i++)
            {
                statements.Add(BuildInsertStatement(tableName, dialect, previewRows[i], headers));
            }

            string previewText = string.Join("\n", statements);
            if (table.Rows.Count > maxPreviewRows)
            {
                previewText += "\n" + SR.GetString("TablePreviewTruncated");
            }

            string defaultTargetDir = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            if (string.IsNullOrWhiteSpace(defaultTargetDir))
            {
                defaultTargetDir = System.IO.Directory.GetCurrentDirectory();
            }
            var plan = _planner.PlanOutput("Export.sql", defaultTargetDir, ".sql", overwrite: false);

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
        string tableName = ExtractTableName(parameters);
        string dialect = ExtractDialect(parameters);

        string outputDir = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        if (string.IsNullOrWhiteSpace(outputDir))
        {
            outputDir = System.IO.Directory.GetCurrentDirectory();
        }
        if (parameters is DictionaryParameters dict && dict.Values.TryGetValue("OutputDirectory", out var paramDir) && !string.IsNullOrWhiteSpace(paramDir))
        {
            outputDir = paramDir;
        }

        string baseFileName = "Export.sql";
        if (parameters is DictionaryParameters dictName && dictName.Values.TryGetValue("BaseFileName", out var paramName) && !string.IsNullOrWhiteSpace(paramName))
        {
            baseFileName = paramName;
        }

        OutputPlan? plan = null;
        bool outputCreated = false;
        try
        {
            plan = _planner.PlanOutput(baseFileName, outputDir, ".sql", overwrite: false);
            _planner.PrepareOutputFolder(plan);

            using (var stream = OpenNewOutput(plan.TargetPath, ref outputCreated))
            using (var writer = new System.IO.StreamWriter(stream, new System.Text.UTF8Encoding(false)))
            {
                FileKit.Tabular.SqlInsertWriter.Write(writer, TableMapping.Map(table, firstRowAsHeader), tableName, dialect, cancellationToken);
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
