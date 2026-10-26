namespace ContextBar.Core.Activation;

public abstract record ActivationMessage;
public sealed record ActivatePaletteMessage : ActivationMessage;
public sealed record ExplicitSelectionMessage(IReadOnlyList<string> FilePaths, string? ActionId = null) : ActivationMessage;

public interface IHostElector : IDisposable
{
    bool Elect();
    Task StartListeningAsync(Func<ActivationMessage, CancellationToken, Task<bool>> receive, CancellationToken cancellationToken);
    Task<bool> SendMessageAsync(ActivationMessage message, CancellationToken cancellationToken);
}

public enum HostStartupResult { Host, Forwarded, Unavailable }

public static class HostStartup
{
    public static async Task<HostStartupResult> ResolveAsync(IHostElector elector, ActivationMessage message,
        CancellationToken cancellationToken, Func<CancellationToken, Task>? retryDelay = null)
    {
        retryDelay ??= token => Task.Delay(100, token);
        // A failed redirect never starts an unelected host. Re-election handles a crashed owner.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (elector.Elect()) return HostStartupResult.Host;
            if (await elector.SendMessageAsync(message, cancellationToken).ConfigureAwait(false))
                return HostStartupResult.Forwarded;
            await retryDelay(cancellationToken).ConfigureAwait(false);
        }
        return elector.Elect() ? HostStartupResult.Host : HostStartupResult.Unavailable;
    }
}
