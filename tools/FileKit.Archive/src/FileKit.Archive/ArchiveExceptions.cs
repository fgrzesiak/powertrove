namespace FileKit.Archive;

/// <summary>Base type for all expected archive failures. Messages never contain entry names or content.</summary>
public class ArchiveException : Exception
{
    public ArchiveException(string message) : base(message) { }
    public ArchiveException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>The archive is damaged, encrypted or not a ZIP file.</summary>
public sealed class InvalidArchiveException : ArchiveException
{
    public InvalidArchiveException(string message) : base(message) { }
    public InvalidArchiveException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>A configured limit (entries, size, compression ratio, depth) was exceeded.</summary>
public sealed class ArchiveLimitExceededException : ArchiveException
{
    public ArchiveLimitExceededException(string message) : base(message) { }
}

/// <summary>An entry would escape the destination or is otherwise unsafe to extract.</summary>
public sealed class UnsafeArchiveEntryException : ArchiveException
{
    public UnsafeArchiveEntryException(string message) : base(message) { }
}
