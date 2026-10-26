using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PowerTrove.Pack.Media;
using Xunit;

namespace PowerTrove.Pack.Media.Tests;

public class FFmpegServiceTests
{
    [Fact]
    public void IsAvailable_ReturnsFalse_WhenExecutableNotFound()
    {
        var service = new FFmpegService("/invalid/path/to/ffmpeg");
        Assert.False(service.IsAvailable);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsError_WhenExecutableNotFound()
    {
        var service = new FFmpegService("/invalid/path/to/ffmpeg");
        var result = await service.ExecuteAsync(new[] { "-version" }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("not found", result.Error);
        Assert.Equal(-1, result.ExitCode);
    }

    [Fact]
    public async Task ExecuteAsync_CancelsProcess_WhenTokenCanceled()
    {
        // Require a real executable or mock for testing cancellation.
        // We will create a dummy script that sleeps and mock it as ffmpeg for Linux.
        var dummyScript = "dummy_ffmpeg.sh";
        if (!OperatingSystem.IsWindows())
        {
            await File.WriteAllTextAsync(dummyScript, "#!/bin/bash\nsleep 10\n");
            var chmodProcess = System.Diagnostics.Process.Start("chmod", $"+x {dummyScript}");
            await chmodProcess.WaitForExitAsync();

            var service = new FFmpegService(Path.Combine(Directory.GetCurrentDirectory(), dummyScript));
            var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

            var result = await service.ExecuteAsync(new[] { "dummy" }, cts.Token);

            Assert.False(result.Success);
            Assert.Contains("canceled", result.Error);
            Assert.Equal(-2, result.ExitCode);

            File.Delete(dummyScript);
        }
    }
}
