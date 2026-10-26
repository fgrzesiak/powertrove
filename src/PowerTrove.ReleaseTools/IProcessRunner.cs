using System.Diagnostics;

namespace PowerTrove.ReleaseTools;

public interface IProcessRunner
{
    Task<(int ExitCode, string StandardOutput, string StandardError)> RunAsync(string fileName, string arguments, string? workingDirectory = null);
}

public class RealProcessRunner : IProcessRunner
{
    public async Task<(int ExitCode, string StandardOutput, string StandardError)> RunAsync(string fileName, string arguments, string? workingDirectory = null)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        return (process.ExitCode, stdout, stderr);
    }
}
