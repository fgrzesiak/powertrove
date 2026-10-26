namespace PowerTrove.Core.Extensibility.PackProtocol;

/// <summary>
/// Incremental, in-memory framing for a future transport. A fatal line or EOF closes this decoder;
/// callers must end the session rather than attempting to recover at the next newline.
/// </summary>
public sealed class PackLineDecoder
{
    private byte[] _buffer = new byte[4096];
    private int _count;
    private bool _closed;

    public IReadOnlyList<PackMessage> Append(ReadOnlySpan<byte> chunk)
    {
        EnsureOpen();
        var messages = new List<PackMessage>();
        try
        {
            while (!chunk.IsEmpty)
            {
                int newline = chunk.IndexOf((byte)'\n');
                Buffer(newline < 0 ? chunk : chunk[..newline]);
                if (newline < 0) break;
                var message = PackMessageSerializer.DeserializeLine(_buffer.AsSpan(0, _count));
                _count = 0;
                if (message is not null) messages.Add(message);
                chunk = chunk[(newline + 1)..];
            }
            return messages.AsReadOnly();
        }
        catch (PackProtocolException)
        {
            _closed = true; _count = 0; throw;
        }
    }

    /// <summary>EOF permits a final valid JSON object without LF, but never a truncated object.</summary>
    public PackMessage? Complete()
    {
        EnsureOpen();
        _closed = true;
        try { return _count == 0 ? null : PackMessageSerializer.DeserializeLine(_buffer.AsSpan(0, _count)); }
        finally { _count = 0; }
    }

    private void Buffer(ReadOnlySpan<byte> bytes)
    {
        // One extra CR is permitted only as part of a CRLF terminator.
        int maximum = PackProtocolLimits.MaximumLineBytes + 1;
        if (bytes.Length > maximum - _count) throw new PackProtocolException(PackProtocolError.LineTooLarge);
        int total = _count + bytes.Length;
        if (total == maximum && (bytes.IsEmpty ? _buffer[_count - 1] : bytes[^1]) != (byte)'\r')
            throw new PackProtocolException(PackProtocolError.LineTooLarge);
        if (total > _buffer.Length) Array.Resize(ref _buffer, Math.Min(maximum, Math.Max(total, _buffer.Length * 2)));
        bytes.CopyTo(_buffer.AsSpan(_count));
        _count = total;
    }

    private void EnsureOpen()
    {
        if (_closed) throw new PackProtocolException(PackProtocolError.SessionClosed);
    }
}
