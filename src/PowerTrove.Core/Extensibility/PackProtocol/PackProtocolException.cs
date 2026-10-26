namespace PowerTrove.Core.Extensibility.PackProtocol;

public static class PackProtocolLimits
{
    public const int Version = 1;
    public const int MaximumLineBytes = 4 * 1024 * 1024;
    public const int MaximumFiles = 10000;
    public const int MaximumPacks = 64;
    public const int MaximumParameters = 1000;
    public const int MaximumTextLength = 4096;
}

public enum PackProtocolError { MalformedJson, LineTooLarge, InvalidMessage, UnsupportedSelection, SessionClosed }

/// <summary>Technical boundary error, never raw JSON, paths, options or an inner parser exception.</summary>
public sealed class PackProtocolException : Exception
{
    public PackProtocolError Code { get; }
    public PackProtocolException(PackProtocolError code) : base("Pack protocol message rejected.") => Code = code;
}
