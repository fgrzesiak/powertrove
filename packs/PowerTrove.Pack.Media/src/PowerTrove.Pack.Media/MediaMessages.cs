using System.Runtime.CompilerServices;

namespace PowerTrove.Pack.Media;

/// <summary>Only messages constructed from this pack's resources may cross an engine boundary.</summary>
public static class MediaMessages
{
    private static readonly ConditionalWeakTable<FileProcessingException, object> SafeMessages = new();

    internal static FileProcessingException Failure(string key, Exception? inner = null)
    {
        var exception = inner is null ? new FileProcessingException(SR.GetString(key)) : new FileProcessingException(SR.GetString(key), inner);
        SafeMessages.Add(exception, new object());
        return exception;
    }

    private static bool IsSafe(Exception exception) => exception is FileProcessingException processing && SafeMessages.TryGetValue(processing, out _);

    public static FileProcessingException InspectFailed(Exception? inner = null) => Failure("MediaInspectFailed", inner);
    public static FileProcessingException ProcessingFailed(Exception? inner = null) => Failure("MediaProcessingFailed", inner);
    internal static FileProcessingException FromEngine(Exception exception) =>
        IsSafe(exception) ? (FileProcessingException)exception : ProcessingFailed(exception);

    internal static string PreviewError(Exception exception) =>
        IsSafe(exception) ? exception.Message : SR.GetString("MediaGifLimit");

    internal static async Task RunAsync(Func<Task> process, CancellationToken cancellationToken)
    {
        try { await process().ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (FileSkippedException) { throw; } // The SDK reports a skip count, never this exception's text.
        catch (FileProcessingException ex) when (IsSafe(ex)) { throw; }
        catch (MediaEngineUnsupportedException ex) { throw Failure("MediaNoEngine", ex); }
        catch (Exception ex) { throw ProcessingFailed(ex); }
    }

    internal static async Task<MediaDetails> InspectAsync(IMediaInspector inspector, string path, CancellationToken cancellationToken)
    {
        try { return await inspector.InspectAsync(path, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (FileProcessingException ex) when (IsSafe(ex)) { throw; }
        catch (Exception ex) { throw InspectFailed(ex); }
    }
}
