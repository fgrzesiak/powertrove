namespace FileKit.Pdf;

/// <summary>Base type for expected PDF failures. Messages never contain document content.</summary>
public class PdfException : Exception
{
    public PdfException(string message) : base(message) { }
    public PdfException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>The file is not a PDF or is too damaged to be repaired.</summary>
public sealed class PdfFormatException : PdfException
{
    public PdfFormatException(string message) : base(message) { }
    public PdfFormatException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>The document is encrypted (password protection or permission restrictions).</summary>
public class PdfEncryptedException : PdfException
{
    public PdfEncryptedException() : base("Encrypted or password-protected PDFs are not supported.") { }
    public PdfEncryptedException(string message) : base(message) { }
}

/// <summary>The document requires a password to be opened, or the provided password was incorrect.</summary>
public sealed class PdfPasswordRequiredException : PdfEncryptedException
{
    public PdfPasswordRequiredException() : base("The document is password-protected.") { }
}

/// <summary>A configured safety limit (size, objects, nesting, decompression) was exceeded.</summary>
public sealed class PdfLimitExceededException : PdfException
{
    public PdfLimitExceededException(string message) : base(message) { }
}

/// <summary>Invalid arguments such as a page range outside the document.</summary>
public sealed class PdfOperationException : PdfException
{
    public PdfOperationException(string message) : base(message) { }
}

/// <summary>Limits applied while reading untrusted PDF input.</summary>
public sealed record PdfReadLimits
{
    public long MaxFileBytes { get; init; } = 1024L * 1024 * 1024;
    public int MaxObjects { get; init; } = 2_000_000;
    public int MaxNestingDepth { get; init; } = 100;
    public int MaxPages { get; init; } = 50_000;
    public long MaxDecodedStreamBytes { get; init; } = 256L * 1024 * 1024;
    public int MaxXrefSections { get; init; } = 1_000;

    public static PdfReadLimits Default { get; } = new();
}
