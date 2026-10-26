using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace PowerTrove.Abstractions.Testing;

/// <summary>
/// Media test fixtures. WAV files are synthesized in code; video fixtures need a
/// locally installed FFmpeg and are skipped (null) when it is missing.
/// </summary>
public static class MediaFixtures
{
    private static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Writes a 16-bit PCM mono WAV with a sine tone.</summary>
    public static string WriteSineWav(string path, double seconds = 1, int sampleRate = 44100, double frequency = 440)
    {
        int samples = (int)(seconds * sampleRate);
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.ASCII);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + samples * 2);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);       // PCM
        writer.Write((short)1);       // mono
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2); // byte rate
        writer.Write((short)2);       // block align
        writer.Write((short)16);      // bits per sample
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(samples * 2);
        for (int i = 0; i < samples; i++)
            writer.Write((short)(Math.Sin(2 * Math.PI * frequency * i / sampleRate) * short.MaxValue * 0.3));
        return path;
    }

    public static bool IsFFmpegAvailable()
    {
        try
        {
            return RunTool("ffmpeg", "-version").ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Generates a one-second H.264/AAC MP4, or returns null when FFmpeg is unavailable.</summary>
    public static string? TryGenerateVideoWithAudio(string outputPath)
    {
        if (!IsFFmpegAvailable()) return null;
        if (File.Exists(outputPath)) File.Delete(outputPath);
        var (exitCode, _, _) = RunTool("ffmpeg",
            $"-nostdin -y -f lavfi -i testsrc=duration=1:size=320x240:rate=30 -f lavfi -i sine=frequency=1000:duration=1 -c:v libx264 -pix_fmt yuv420p -c:a aac \"{outputPath}\"");
        return exitCode == 0 && File.Exists(outputPath) ? outputPath : null;
    }

    public static bool HasAudioStream(string mediaPath)
    {
        var (_, output, _) = RunTool("ffprobe",
            $"-v error -select_streams a -show_entries stream=codec_type -of default=noprint_wrappers=1:nokey=1 \"{mediaPath}\"");
        return !string.IsNullOrWhiteSpace(output) && output.Contains("audio");
    }

    // Drains stdout and stderr concurrently: ffmpeg writes progress to stderr, and
    // waiting for exit before reading a full pipe deadlocks the test run.
    private static (int ExitCode, string Output, string Error) RunTool(string fileName, string arguments)
    {
        using var process = new Process();
        process.StartInfo.FileName = fileName;
        process.StartInfo.Arguments = arguments;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardInput = true;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.CreateNoWindow = true;
        process.Start();
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(ToolTimeout))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{fileName} did not finish within {ToolTimeout.TotalSeconds} seconds.");
        }
        return (process.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult());
    }
}
