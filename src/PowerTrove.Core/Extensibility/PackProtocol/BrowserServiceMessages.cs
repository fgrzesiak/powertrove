using PowerTrove.Abstractions;

namespace PowerTrove.Core.Extensibility.PackProtocol;

/// <summary>Worker requests browser data only for its own active, authorized action.</summary>
public sealed record BrowserServiceRequestMessage(Guid RequestId, Guid OperationId,
    BrowserPageOperation Operation) : PackMessage
{
    public override string Type => "browserServiceRequest";
}

public sealed record BrowserServiceResultMessage(Guid RequestId, Guid OperationId,
    BrowserPageContent? Content) : PackMessage
{
    public override string Type => "browserServiceResult";
}

    
/// <summary>
/// Host-to-worker bounded binary transfer. Correlated to the active action and
/// browser operation, never includes a local source path. Only the worker stages it.
/// </summary>
public sealed record BrowserServiceBinaryChunkMessage(
    Guid RequestId, Guid OperationId, int Sequence, bool Last, string Kind, string Base64) : PackMessage
{
    public override string Type => "browserServiceBinaryChunk";
}
