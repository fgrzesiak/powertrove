using PowerTrove.Core.Extensibility.PackProtocol;

namespace PowerTrove.FakePackWorker;

public sealed class Marker { }

/// <summary>Generated transport fixture. Never shipped or used as a production fallback.</summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        string? pack = null, work = null;
        for (int i = 0; i + 1 < args.Length; i += 2)
        {
            if (args[i] == "--pack-dir") pack = args[i + 1];
            if (args[i] == "--work-root") work = args[i + 1];
        }
        if (work is not null) await File.WriteAllTextAsync(Path.Combine(work, "process-id.txt"), Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        string mode = Path.GetFileName(pack ?? "success");
        using var input = Console.OpenStandardInput();
        using var output = Console.OpenStandardOutput();
        using var writer = new PackMessageWriter(output);
        Guid? active = null;
        await foreach (var message in PackMessageStream.ReadAsync(input))
        {
            switch (message)
            {
                case HelloMessage when mode != "startup-hang":
                    await writer.WriteAsync(new ReadyMessage([new WorkerPack("contextbar.fixture", "0.1.0")])); break;
                case PreviewMessage preview:
                    await writer.WriteAsync(new PreviewResultMessage(preview.RequestId, true, "Generated fixture preview", [], [], false)); break;
                case ExecuteMessage execute:
                    if (mode == "crash") return 13;
                    active = execute.RequestId;
                    await writer.WriteAsync(new ProgressMessage(execute.RequestId, 0.1));
                    if (mode is "cancel" or "hang") break;
                    await File.WriteAllTextAsync(Path.Combine(execute.StagingDirectory, "generated.txt"), "generated");
                    string directory = Path.GetDirectoryName(execute.Selection.Files[0].Path)!;
                    if (mode == "bad-output") directory = Path.Combine(directory, "unapproved");
                    await writer.WriteAsync(new ResultMessage(execute.RequestId,
                        new ActionReceipt(true, false, ["generated.txt"], [], false),
                        [new StagedOutput("generated.txt", directory, "copy.txt")]));
                    active = null; break;
                case CancelMessage cancel when active == cancel.RequestId && mode == "cancel":
                    await writer.WriteAsync(new ResultMessage(cancel.RequestId, new ActionReceipt(false, true, [], [], false), []));
                    active = null; break;
                case ShutdownMessage: return 0;
            }
        }
        return 0;
    }
}
