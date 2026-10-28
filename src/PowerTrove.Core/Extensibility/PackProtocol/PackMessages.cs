using System.Text.Json.Serialization;

namespace PowerTrove.Core.Extensibility.PackProtocol;

/// <summary>Wire messages contain sensitive paths/options: never log or interpolate their payloads.</summary>
public abstract record PackMessage
{
    public abstract string Type { get; }
    public sealed override string ToString() => $"PackMessage {{ Type = {Type} }}";
}

public sealed record HelloMessage(
    [property: JsonRequired] int ProtocolVersion,
    [property: JsonRequired] string HostVersion) : PackMessage
{
    public override string Type => "hello";
}

public sealed record ReadyMessage([property: JsonRequired] IReadOnlyList<WorkerPack> Packs) : PackMessage
{
    public override string Type => "ready";
}
public sealed record WorkerPack([property: JsonRequired] string Id, [property: JsonRequired] string Version);

public sealed record PreviewMessage(
    [property: JsonRequired] Guid RequestId,
    [property: JsonRequired] string ActionId,
    [property: JsonRequired] FileSelectionPayload Selection,
    [property: JsonRequired] IReadOnlyDictionary<string, string> Parameters,
    [property: JsonRequired] DateTimeOffset ExpiresAt,
    TableSelectionPayload? Table = null) : PackMessage
{
    public override string Type => "preview";
}

/// <summary>A request carries either a non-empty file selection or a table with an empty file selection.</summary>
public sealed record ExecuteMessage(
    [property: JsonRequired] Guid RequestId,
    [property: JsonRequired] string ActionId,
    [property: JsonRequired] FileSelectionPayload Selection,
    [property: JsonRequired] IReadOnlyDictionary<string, string> Parameters,
    [property: JsonRequired] DateTimeOffset ExpiresAt,
    [property: JsonRequired] string StagingDirectory,
    TableSelectionPayload? Table = null) : PackMessage
{
    public override string Type => "execute";
}

// ActionPreview fields are flattened on the wire, with no polymorphic SDK context/parameters.
public sealed record PreviewResultMessage(
    [property: JsonRequired] Guid RequestId,
    [property: JsonRequired] bool RequiresConfirmation,
    string? PreviewText,
    [property: JsonRequired] IReadOnlyList<string> ExpectedOutputs,
    [property: JsonRequired] IReadOnlyList<string> Warnings,
    [property: JsonRequired] bool IsBlocked,
    IReadOnlyList<RenamePreviewRow>? RenameRows = null,
    ActionConfirmation? Confirmation = null,
    IReadOnlyList<ActionParameterChoiceSet>? ParameterChoices = null) : PackMessage
{
    public override string Type => "previewResult";
    public static PreviewResultMessage FromPreview(Guid requestId, ActionPreview preview) => new(requestId,
        preview.RequiresConfirmation, preview.PreviewText, preview.ExpectedOutputs, preview.Warnings,
        preview.IsBlocked, preview.RenameRows, preview.Confirmation, preview.ParameterChoices);
    public ActionPreview ToPreview() => new(RequiresConfirmation, PreviewText, ExpectedOutputs, Warnings,
        IsBlocked, RenameRows, Confirmation, ParameterChoices);
}

public sealed record ProgressMessage(
    [property: JsonRequired] Guid RequestId,
    [property: JsonRequired] double Fraction,
    string? Message = null) : PackMessage
{
    public override string Type => "progress";
}

public sealed record ResultMessage(
    [property: JsonRequired] Guid RequestId,
    [property: JsonRequired] ActionReceipt Receipt,
    [property: JsonRequired] IReadOnlyList<StagedOutput> Outputs) : PackMessage
{
    public override string Type => "result";
}

/// <summary>Path is relative to the request's staging directory, using forward slashes.</summary>
public sealed record StagedOutput(
    [property: JsonRequired] string Path,
    [property: JsonRequired] string IntendedDirectory,
    [property: JsonRequired] string IntendedName)
{
    public override string ToString() => "StagedOutput { Payload = redacted }";
}

public sealed record CancelMessage([property: JsonRequired] Guid RequestId) : PackMessage
{
    public override string Type => "cancel";
}

public sealed record ErrorMessage(
    [property: JsonRequired] string Code,
    [property: JsonRequired] string Message,
    Guid? RequestId = null) : PackMessage
{
    public override string Type => "error";
}

public sealed record HeartbeatMessage([property: JsonRequired] Guid RequestId) : PackMessage
{
    public override string Type => "heartbeat";
}

public sealed record ShutdownMessage : PackMessage
{
    public override string Type => "shutdown";
}

public sealed record FileSelectionPayload([property: JsonRequired] IReadOnlyList<ProtocolFile> Files)
{
    /// <summary>Rejects all non-file snapshots before serializing any captured content.</summary>
    public static FileSelectionPayload FromSnapshot(ContextSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Status != ContextStatus.Available || snapshot.Content is not FileSelection selection)
            throw new PackProtocolException(PackProtocolError.UnsupportedSelection);
        if (selection.Files is null || selection.Files.Count > PackProtocolLimits.MaximumFiles || selection.Files.Any(file => file is null))
            throw new PackProtocolException(PackProtocolError.InvalidMessage);
        var payload = new FileSelectionPayload(Array.AsReadOnly(selection.Files.Select(file => new ProtocolFile(
            file.Path!, file.Name, file.Extension, file.IsDirectory, file.SizeBytes)).ToArray()));
        PackMessageValidation.ValidateSelection(payload);
        return payload;
    }
    public override string ToString() => $"FileSelectionPayload {{ Count = {Files?.Count ?? 0} }}";
}

/// <summary>
/// Captured table cells (for example an Excel range). Contents are user data: never log, interpolate or
/// persist them. Workers may write outputs only below <see cref="OutputDirectory"/>, which the host chose.
/// </summary>
public sealed record TableSelectionPayload(
    [property: JsonRequired] IReadOnlyList<IReadOnlyList<ProtocolCell>> Rows,
    [property: JsonRequired] string OutputDirectory,
    string? SourceApplication = null,
    string? SourceRange = null)
{
    public static TableSelectionPayload FromSnapshot(ContextSnapshot snapshot, string outputDirectory)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Status != ContextStatus.Available || snapshot.Content is not TableData table)
            throw new PackProtocolException(PackProtocolError.UnsupportedSelection);
        if (table.Rows is null || table.Rows.Count > PackProtocolLimits.MaximumTableCells || table.Rows.Any(row => row is null))
            throw new PackProtocolException(PackProtocolError.InvalidMessage);
        if (table.Rows.Sum(row => (long)row.Count) > PackProtocolLimits.MaximumTableCells)
            throw new PackProtocolException(PackProtocolError.LineTooLarge);
        var payload = new TableSelectionPayload(Array.AsReadOnly(table.Rows.Select(row =>
            (IReadOnlyList<ProtocolCell>)Array.AsReadOnly(row.Select(ProtocolCell.From).ToArray())).ToArray()),
            outputDirectory, table.SourceApplication, table.SourceRange);
        PackMessageValidation.ValidateTable(payload);
        return payload;
    }
    public TableData ToTableData() => new(Array.AsReadOnly(Rows.Select(row =>
        (IReadOnlyList<CellValue>)Array.AsReadOnly(row.Select(cell => cell.ToCellValue()).ToArray())).ToArray()), SourceApplication, SourceRange);
    public override string ToString() => $"TableSelectionPayload {{ Rows = {Rows?.Count ?? 0} }}";
}

/// <summary>One typed cell; short member names keep large ranges within the line limit.</summary>
public sealed record ProtocolCell(
    [property: JsonRequired, JsonPropertyName("k")] string Kind,
    [property: JsonPropertyName("s")] string? Text = null,
    [property: JsonPropertyName("n")] double? Number = null,
    [property: JsonPropertyName("b")] bool? Boolean = null,
    [property: JsonPropertyName("d")] DateTime? Date = null,
    [property: JsonPropertyName("f")] string? Formatted = null)
{
    public static ProtocolCell From(CellValue cell) => cell switch
    {
        EmptyCell => new("empty"),
        TextCell t => new("text", Text: t.Value, Formatted: t.FormattedValue),
        NumberCell n => new("number", Number: n.Value, Formatted: n.FormattedValue),
        BooleanCell b => new("boolean", Boolean: b.Value, Formatted: b.FormattedValue),
        DateCell d => new("date", Date: d.Value, Formatted: d.FormattedValue),
        ErrorCell e => new("error", Text: e.Code, Formatted: e.FormattedValue),
        _ => throw new PackProtocolException(PackProtocolError.InvalidMessage)
    };
    public CellValue ToCellValue() => Kind switch
    {
        "empty" => new EmptyCell(),
        "text" => new TextCell(Text!, Formatted),
        "number" => new NumberCell(Number!.Value, Formatted),
        "boolean" => new BooleanCell(Boolean!.Value, Formatted),
        "date" => new DateCell(Date!.Value, Formatted),
        "error" => new ErrorCell(Text!, Formatted),
        _ => throw new PackProtocolException(PackProtocolError.InvalidMessage)
    };
    public override string ToString() => "ProtocolCell { Payload = redacted }";
}

public sealed record ProtocolFile(
    [property: JsonRequired] string Path,
    [property: JsonRequired] string Name,
    [property: JsonRequired] string Extension,
    [property: JsonRequired] bool IsDirectory,
    long? SizeBytes = null)
{
    public override string ToString() => "ProtocolFile { Payload = redacted }";
}
