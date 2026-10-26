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
    [property: JsonRequired] DateTimeOffset ExpiresAt) : PackMessage
{
    public override string Type => "preview";
}

public sealed record ExecuteMessage(
    [property: JsonRequired] Guid RequestId,
    [property: JsonRequired] string ActionId,
    [property: JsonRequired] FileSelectionPayload Selection,
    [property: JsonRequired] IReadOnlyDictionary<string, string> Parameters,
    [property: JsonRequired] DateTimeOffset ExpiresAt,
    [property: JsonRequired] string StagingDirectory) : PackMessage
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
    ActionConfirmation? Confirmation = null) : PackMessage
{
    public override string Type => "previewResult";
    public static PreviewResultMessage FromPreview(Guid requestId, ActionPreview preview) => new(requestId,
        preview.RequiresConfirmation, preview.PreviewText, preview.ExpectedOutputs, preview.Warnings,
        preview.IsBlocked, preview.RenameRows, preview.Confirmation);
    public ActionPreview ToPreview() => new(RequiresConfirmation, PreviewText, ExpectedOutputs, Warnings,
        IsBlocked, RenameRows, Confirmation);
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

public sealed record ProtocolFile(
    [property: JsonRequired] string Path,
    [property: JsonRequired] string Name,
    [property: JsonRequired] string Extension,
    [property: JsonRequired] bool IsDirectory,
    long? SizeBytes = null)
{
    public override string ToString() => "ProtocolFile { Payload = redacted }";
}
