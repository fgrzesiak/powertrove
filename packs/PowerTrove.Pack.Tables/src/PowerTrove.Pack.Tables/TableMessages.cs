namespace PowerTrove.Pack.Tables;

/// <summary>Maps export failures without exposing paths, selected values or raw tool text.</summary>
internal static class TableMessages
{
    public static string Describe(Exception exception) => SR.GetString(exception switch
    {
        UnauthorizedAccessException => "TablePermissionDenied",
        IOException => "TableSaveFailed",
        FileKit.Tabular.TabularException => "TableInvalidData",
        ArgumentException or NotSupportedException => "TableInvalidOptions",
        _ => "TableExportFailed"
    });
}
