using System;
using System.Collections.Generic;

namespace ContextBar.Abstractions;

public enum ContentKind { None, Files, Table, Text }
public enum ContextStatus { Available, Empty, Unsupported, Denied, TimedOut, Failed, Canceled }

public sealed record SourceWindowIdentity(
    nint WindowHandle,
    int ProcessId,
    string? ProcessName,
    DateTimeOffset CapturedAt,
    string? Title = null);

public abstract record ContextContent;
public sealed record NoContent() : ContextContent;
public sealed record FileSelection(
    IReadOnlyList<SelectedFile> Files) : ContextContent
{
    public override string ToString() => $"FileSelection {{ Count = {Files.Count} }}";
}
public sealed record TextSelection(string Text) : ContextContent;
public sealed record TableData(
    IReadOnlyList<IReadOnlyList<CellValue>> Rows,
    string? SourceApplication,
    string? SourceRange) : ContextContent;

public sealed record SelectedFile(
    string? Path,
    string Name,
    string Extension,
    string? MediaType,
    long? SizeBytes,
    bool IsAvailableLocally,
    bool IsAccessible,
    IReadOnlyDictionary<string, string>? Metadata,
    bool IsDirectory = false)
{
    public override string ToString() => $"SelectedFile {{ Extension = {Extension}, MediaType = {MediaType}, SizeBytes = {SizeBytes}, IsAvailableLocally = {IsAvailableLocally}, IsAccessible = {IsAccessible}, IsDirectory = {IsDirectory} }}";
}

public abstract record CellValue(string? FormattedValue = null);
public sealed record EmptyCell() : CellValue((string?)null);
public sealed record TextCell(string Value, string? FormattedValue = null) : CellValue(FormattedValue);
public sealed record NumberCell(double Value, string? FormattedValue = null) : CellValue(FormattedValue);
public sealed record BooleanCell(bool Value, string? FormattedValue = null) : CellValue(FormattedValue);
public sealed record DateCell(DateTime Value, string? FormattedValue = null) : CellValue(FormattedValue);
public sealed record ErrorCell(string Code, string? FormattedValue = null) : CellValue(FormattedValue);

public sealed record ContextSnapshot(
    Guid InvocationId,
    SourceWindowIdentity Source,
    ContextStatus Status,
    ContextContent Content,
    DateTimeOffset CapturedAt,
    DateTimeOffset ExpiresAt,
    string ProviderId,
    string? UserMessage,
    IReadOnlySet<string>? ProvidedCapabilities = null)
{
    public bool IsStale => DateTimeOffset.UtcNow > ExpiresAt;
}
