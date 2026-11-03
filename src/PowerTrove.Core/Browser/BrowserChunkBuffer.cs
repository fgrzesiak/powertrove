namespace PowerTrove.Core.Browser;

/// <summary>
/// Bounded in-memory reassembly of a single ordered Browser Companion transfer.
/// An aborted transfer never exposes partial data to an action.
/// </summary>
public sealed class BrowserChunkBuffer : IDisposable
{
    private readonly Guid _id;
    private readonly string _kind;
    private readonly MemoryStream _output = new();
    private int _nextSequence;
    private bool _complete;
    private bool _disposed;

    public BrowserChunkBuffer(Guid id, string kind)
    {
        if (id == Guid.Empty) throw new ArgumentException("Transfer ID is required.", nameof(id));
        _ = BrowserWireProtocol.MaximumTotalBytes(kind);
        _id = id;
        _kind = kind;
    }

    public bool IsComplete => _complete;
    public int Length => checked((int)_output.Length);

    public void Append(BrowserWireProtocol.Chunk chunk, CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        token.ThrowIfCancellationRequested();
        if (_complete || chunk.Id != _id || chunk.Kind != _kind || chunk.Seq != _nextSequence)
            throw new InvalidDataException("Invalid browser transfer sequence.");
        if (chunk.Base64.Length > 4 * ((BrowserWireProtocol.MaximumChunkBytes + 2) / 3))
            throw new InvalidDataException("Browser transfer chunk exceeds the limit.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(chunk.Base64); }
        catch (FormatException) { throw new InvalidDataException("Invalid browser transfer encoding."); }
        if (!string.Equals(Convert.ToBase64String(bytes), chunk.Base64, StringComparison.Ordinal) ||
            bytes.Length is 0 or > BrowserWireProtocol.MaximumChunkBytes ||
            _output.Length + bytes.Length > BrowserWireProtocol.MaximumTotalBytes(_kind))
            throw new InvalidDataException("Browser transfer exceeds its size limit.");
        token.ThrowIfCancellationRequested();
        _output.Write(bytes);
        _nextSequence++;
        _complete = chunk.Last;
    }

    public byte[] Complete()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_complete) throw new InvalidOperationException("Browser transfer is incomplete.");
        return _output.ToArray();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _output.Dispose();
    }
}
