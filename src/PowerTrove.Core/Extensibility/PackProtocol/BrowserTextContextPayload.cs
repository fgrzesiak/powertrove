namespace PowerTrove.Core.Extensibility.PackProtocol;

/// <summary>
/// Browser metadata explicitly authorized by the host. Never log this record:
/// title and URL may contain private information.
/// </summary>
public sealed record BrowserTextContextPayload(string Url, string Title,
    PowerTrove.Abstractions.BrowserTabHandle? Tab = null)
{
    public override string ToString() => "BrowserTextContextPayload { Redacted }";
}
