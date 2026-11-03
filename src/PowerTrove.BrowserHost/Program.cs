using System.Diagnostics;
using System.IO.Pipes;
using System.Threading.Channels;
using PowerTrove.Core.Browser;
using System.Buffers.Binary;
using System.Security.Principal;
using Microsoft.Win32;
using PowerTrove.Core.Activation;

namespace PowerTrove.BrowserHost;

/// <summary>Never prints untrusted content or diagnostics to stdout: stdout is reserved for Chrome framing.</summary>
public static class Program
{
    private const string SettingsKey = @"Software\PowerTrove\BrowserHost";

    public static async Task<int> Main(string[] args)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        bool success = false;
        try
        {
            // The installed Chromium manifest restricts origins; independently enforce
            // the user-approved origin stored by PowerTrove. stdout is protocol-only.
            string? sender = args.FirstOrDefault();
            if (IsExtensionOrigin(sender) && IsRegisteredOrigin(sender!))
            {
                using var input = Console.OpenStandardInput();
                using var output = Console.OpenStandardOutput();
                byte[]? first = await ReadNativePayloadAsync(input, timeout.Token);
                if (first is not null && BrowserWireProtocol.Decode(first) is BrowserWireProtocol.Hello greeting)
                {
                    // Session mode is intentionally long-lived; legacy sendNativeMessage
                    // below remains unchanged for old extension versions.
                    return await RunSessionAsync(greeting, input, output);
                }
                if (first is not null)
                {
                    var message = NativeMessagingProtocol.DecodeActivation(first);
                    if (message is not null) success = await DeliverAsync(message, timeout.Token);
                }
                await NativeMessagingProtocol.WriteResponseAsync(output, success, CancellationToken.None);
                return 0;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or OperationCanceledException or System.Text.Json.JsonException or EndOfStreamException
            or System.ComponentModel.Win32Exception or System.Security.SecurityException or ArgumentException)
        {
            // Never log untrusted page title, URL, selection, or raw exception details.
        }
        await NativeMessagingProtocol.WriteResponseAsync(Console.OpenStandardOutput(), false, CancellationToken.None);
        return 0;
    }

    private static async Task<byte[]?> ReadNativePayloadAsync(Stream input, CancellationToken token)
    {
        byte[] header = new byte[4];
        await input.ReadExactlyAsync(header, token);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > BrowserWireProtocol.MaximumMessageBytes) return null;
        var content = new byte[length];
        await input.ReadExactlyAsync(content, token);
        return content;
    }

    private static async Task<int> RunSessionAsync(BrowserWireProtocol.Hello hello, Stream input, Stream output)
    {
        using var identity = WindowsIdentity.GetCurrent();
        string sid = identity.User?.Value ?? throw new InvalidOperationException("SID unavailable");
        int session = Process.GetCurrentProcess().SessionId;
        // Separate pipe from one-shot activation. CurrentUserOnly on both ends.
        using var pipe = new NamedPipeClientStream(".", $"PowerTrove_Browser_{sid}_{session}",
            PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var running = new CancellationTokenSource();
        // Read the browser side from the start: it ends the wait below when the port closes.
        var fromBrowser = Channel.CreateBounded<BrowserWireProtocol.Frame>(
            new BoundedChannelOptions(32) { SingleReader = true, SingleWriter = true });
        var pump = PumpAsync(input, fromBrowser.Writer, running.Token);
        try
        {
            // Wait for PowerTrove instead of exiting: the open port keeps the extension's
            // service worker alive, so the tab context is ready as soon as the app starts.
            while (!pipe.IsConnected)
            {
                if (pump.IsCompleted) return 0;
                try { await pipe.ConnectAsync(500, running.Token); }
                catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
                {
                    await Task.WhenAny(pump, Task.Delay(1500, running.Token));
                }
            }
            await BrowserWireFraming.WriteAsync(pipe, hello, running.Token);
            var ready = await BrowserWireFraming.ReadAsync(pipe, running.Token);
            if (ready is not BrowserWireProtocol.Ready accepted ||
                BrowserWireProtocol.Negotiate(accepted.MinProtocol, accepted.MaxProtocol) is null) return 0;
            await BrowserWireFraming.WriteAsync(output, accepted, running.Token);

            async Task ToDesktopAsync()
            {
                await foreach (var message in fromBrowser.Reader.ReadAllAsync(running.Token))
                {
                    if (message is BrowserWireProtocol.Hello or BrowserWireProtocol.Ready) break;
                    await BrowserWireFraming.WriteAsync(pipe, message, running.Token);
                }
            }
            async Task ToBrowserAsync()
            {
                while (!running.IsCancellationRequested)
                {
                    var message = await BrowserWireFraming.ReadAsync(pipe, running.Token);
                    if (message is null or BrowserWireProtocol.Hello or BrowserWireProtocol.Ready) break;
                    await BrowserWireFraming.WriteAsync(output, message, running.Token);
                }
            }
            // When PowerTrove exits the port closes too; the extension reconnects and a new host waits again.
            await Task.WhenAny(ToDesktopAsync(), ToBrowserAsync());
            return 0;
        }
        finally { await running.CancelAsync(); }
    }

    private static async Task PumpAsync(Stream input, ChannelWriter<BrowserWireProtocol.Frame> writer,
        CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var message = await BrowserWireFraming.ReadAsync(input, token);
                if (message is null) break; // Invalid frames end the session, as before.
                await writer.WriteAsync(message, token);
            }
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or InvalidDataException
            or OperationCanceledException or ObjectDisposedException) { }
        finally { writer.TryComplete(); }
    }

    public static bool IsExtensionOrigin(string? origin)
    {
        if (origin is null || !origin.StartsWith("chrome-extension://", StringComparison.Ordinal))
            return false;
        string id = origin["chrome-extension://".Length..].TrimEnd('/');
        return id.Length == 32 && id.All(c => c is >= 'a' and <= 'p') &&
            origin is var candidate && (candidate.EndsWith(id, StringComparison.Ordinal) ||
            candidate.EndsWith(id + "/", StringComparison.Ordinal));
    }

    private static bool IsRegisteredOrigin(string origin)
    {
        // Chromium may pass either chrome-extension://id or chrome-extension://id/.
        string normalized = origin.EndsWith("/", StringComparison.Ordinal) ? origin : origin + "/";
        string? value = Registry.CurrentUser.OpenSubKey(SettingsKey)?.GetValue("AllowedOrigins") as string;
        return value?.Split(';', StringSplitOptions.RemoveEmptyEntries).Contains(normalized, StringComparer.Ordinal) == true;
    }

    private static async Task<bool> DeliverAsync(ActivationMessage selection, CancellationToken token)
    {
        if (await SendToDesktopAsync(selection, token)) return true;
        string? executable = Registry.CurrentUser.OpenSubKey(SettingsKey)?.GetValue("DesktopExe") as string;
        if (string.IsNullOrWhiteSpace(executable) || !Path.IsPathFullyQualified(executable) ||
            Path.GetFileName(executable) is not ("PowerTrove.exe" or "PowerTrove.Desktop.exe") ||
            !File.Exists(executable)) return false;
        using var launched = Process.Start(new ProcessStartInfo
        {
            FileName = executable, WorkingDirectory = Path.GetDirectoryName(executable)!,
            UseShellExecute = true
        });
        if (launched is null) return false;
        for (int attempt = 0; attempt < 12; attempt++)
        {
            await Task.Delay(450, token).ConfigureAwait(false);
            if (await SendToDesktopAsync(selection, token)) return true;
        }
        return false;
    }

    private static async Task<bool> SendToDesktopAsync(ActivationMessage selection, CancellationToken token)
    {
        using var identity = WindowsIdentity.GetCurrent();
        string sid = identity.User?.Value ?? throw new InvalidOperationException("Windows SID unavailable.");
        int session = Process.GetCurrentProcess().SessionId;
        string name = $"PowerTrove_{sid}_{session}";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(350));
        try
        {
            using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
            await ActivationProtocol.WriteAsync(pipe, selection, timeout.Token).ConfigureAwait(false);
            var answer = new byte[1];
            await pipe.ReadExactlyAsync(answer, timeout.Token).ConfigureAwait(false);
            return answer[0] == 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            return false;
        }
    }
}
