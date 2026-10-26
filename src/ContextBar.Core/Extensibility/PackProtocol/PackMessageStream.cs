using System.Runtime.CompilerServices;

namespace ContextBar.Core.Extensibility.PackProtocol;

/// <summary>Bounded stream framing, shared by the worker and supervisor. No payload logging.</summary>
public static class PackMessageStream
{
    public static async IAsyncEnumerable<PackMessage> ReadAsync(Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var decoder = new PackLineDecoder();
        byte[] buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
            foreach (var message in decoder.Append(buffer.AsSpan(0, count))) yield return message;
        if (decoder.Complete() is { } last) yield return last;
    }
}

public sealed class PackMessageWriter(Stream output) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public async Task WriteAsync(PackMessage message, CancellationToken cancellationToken = default)
    {
        byte[] line = PackMessageSerializer.SerializeLine(message);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await output.WriteAsync(line, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }
    public void Dispose() => _gate.Dispose();
}
