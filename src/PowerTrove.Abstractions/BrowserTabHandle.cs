namespace PowerTrove.Abstractions;

/// <summary>Opaque short-lived browser tab binding. Reject navigation before each operation.</summary>
public sealed record BrowserTabHandle(Guid SessionId, int WindowId, int TabId, string DocumentId)
{
    public override string ToString() => "BrowserTabHandle { Redacted }";
}
