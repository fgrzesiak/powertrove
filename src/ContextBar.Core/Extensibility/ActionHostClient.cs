using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ContextBar.Core.Extensibility;

public class ActionHostClient : IActionHost
{
    private readonly string _executablePath;
    private Process? _process;
    private StreamWriter? _writer;
    private StreamReader? _reader;

    public ActionHostClient(string executablePath)
    {
        _executablePath = executablePath;
    }

    public bool IsHealthy => _process is { HasExited: false };

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_executablePath))
        {
            throw new FileNotFoundException("Plugin host executable not found.", _executablePath);
        }

        _process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _executablePath,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            }
        };

        _process.Start();
        _writer = _process.StandardInput;
        _reader = _process.StandardOutput;

        return Task.CompletedTask;
    }

    public async Task<ActionReceipt> ExecuteActionAsync(
        string actionId,
        ContextSnapshot context,
        ActionParameters parameters,
        IProgress<ActionProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!IsHealthy || _writer == null || _reader == null)
        {
            throw new InvalidOperationException("Host is not healthy.");
        }

        var request = new PluginRequest(
            Guid.NewGuid(),
            actionId,
            PluginMessageSerializer.SerializeContext(context),
            PluginMessageSerializer.SerializeParameters(parameters),
            false);

        var jsonRequest = PluginMessageSerializer.SerializeRequest(request);
        await _writer.WriteLineAsync(jsonRequest).ConfigureAwait(false);

        var jsonResponse = await _reader.ReadLineAsync().ConfigureAwait(false);
        if (jsonResponse == null)
        {
            throw new InvalidOperationException("Host process terminated unexpectedly.");
        }

        var response = PluginMessageSerializer.DeserializeResponse(jsonResponse);
        if (response == null || !response.Success)
        {
            return new ActionReceipt(false, false, Array.Empty<string>(), new[] { new ActionFailure(null, response?.ErrorMessage ?? "Unknown error") }, false, response?.ErrorMessage);
        }

        return JsonSerializer.Deserialize<ActionReceipt>(response.SerializedReceipt!, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!;
    }

    public async Task<ActionPreview> PreviewActionAsync(
        string actionId,
        ContextSnapshot context,
        ActionParameters parameters,
        CancellationToken cancellationToken)
    {
         if (!IsHealthy || _writer == null || _reader == null)
        {
            throw new InvalidOperationException("Host is not healthy.");
        }

        var request = new PluginRequest(
            Guid.NewGuid(),
            actionId,
            PluginMessageSerializer.SerializeContext(context),
            PluginMessageSerializer.SerializeParameters(parameters),
            true);

        var jsonRequest = PluginMessageSerializer.SerializeRequest(request);
        await _writer.WriteLineAsync(jsonRequest).ConfigureAwait(false);

        var jsonResponse = await _reader.ReadLineAsync().ConfigureAwait(false);
        if (jsonResponse == null)
        {
            throw new InvalidOperationException("Host process terminated unexpectedly.");
        }

        var response = PluginMessageSerializer.DeserializeResponse(jsonResponse);
        if (response == null || !response.Success)
        {
            return new ActionPreview(false, response?.ErrorMessage ?? "Unknown error", Array.Empty<string>(), Array.Empty<string>());
        }

        return JsonSerializer.Deserialize<ActionPreview>(response.SerializedPreview!, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!;
    }

    public void Dispose()
    {
        _writer?.Dispose();
        _reader?.Dispose();
        if (_process != null && !_process.HasExited)
        {
            _process.Kill();
            _process.Dispose();
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
