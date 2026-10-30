using FileKit.Media;
using PowerTrove.Platform.Windows.Media;

namespace PowerTrove.Platform.Windows.Tests.Media;

public class FFmpegSetupTests
{
    private sealed class FakeRunner(string? winget, Func<CancellationToken, Task<int>> run) : IWingetRunner
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];
        public string? Locate() => winget;
        public Task<int> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            Calls.Add(arguments);
            return run(cancellationToken);
        }
    }

    [Fact]
    public void InstallArguments_AreFixedPerUserAndNonInteractive()
    {
        Assert.Equal(new[]
        {
            "install", "--id", FFmpegLocator.WingetPackageId, "--exact", "--source", "winget", "--scope", "user",
            "--silent", "--disable-interactivity", "--accept-package-agreements", "--accept-source-agreements"
        }, FFmpegSetup.InstallArguments);
    }

    [Fact]
    public async Task AlreadyInstalled_StartsNoProcess()
    {
        var runner = new FakeRunner("winget.exe", _ => Task.FromResult(0));
        var setup = new FFmpegSetup(runner, () => @"C:\ffmpeg\bin\ffmpeg.exe");
        Assert.Equal(FFmpegInstallOutcome.Installed, await setup.InstallAsync(CancellationToken.None));
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task MissingWinget_IsReportedWithoutAnyProcess()
    {
        var runner = new FakeRunner(null, _ => Task.FromResult(0));
        var setup = new FFmpegSetup(runner, () => null);
        Assert.False(setup.IsWingetAvailable());
        Assert.Equal(FFmpegInstallOutcome.WingetMissing, await setup.InstallAsync(CancellationToken.None));
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task SuccessfulRun_IsInstalledOnlyWhenFFmpegIsFoundAfterwards()
    {
        string? found = null;
        var runner = new FakeRunner("winget.exe", _ => { found = @"C:\Links\ffmpeg.exe"; return Task.FromResult(0); });
        Assert.Equal(FFmpegInstallOutcome.Installed, await new FFmpegSetup(runner, () => found).InstallAsync(CancellationToken.None));
        Assert.Equal(FFmpegSetup.InstallArguments, Assert.Single(runner.Calls));

        var silent = new FakeRunner("winget.exe", _ => Task.FromResult(0));
        Assert.Equal(FFmpegInstallOutcome.NotFoundAfterInstall, await new FFmpegSetup(silent, () => null).InstallAsync(CancellationToken.None));
    }

    [Fact]
    public async Task NonZeroExit_IsInstalledWhenFFmpegExists_OtherwiseFailed()
    {
        string? found = null;
        // winget reports "already installed" with a non-zero code; the file is what counts.
        var already = new FakeRunner("winget.exe", _ => { found = @"C:\Links\ffmpeg.exe"; return Task.FromResult(-1978335189); });
        Assert.Equal(FFmpegInstallOutcome.Installed, await new FFmpegSetup(already, () => found).InstallAsync(CancellationToken.None));

        var failing = new FakeRunner("winget.exe", _ => Task.FromResult(1));
        Assert.Equal(FFmpegInstallOutcome.Failed, await new FFmpegSetup(failing, () => null).InstallAsync(CancellationToken.None));

        var broken = new FakeRunner("winget.exe", _ => throw new System.ComponentModel.Win32Exception());
        Assert.Equal(FFmpegInstallOutcome.Failed, await new FFmpegSetup(broken, () => null).InstallAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Cancellation_IsReportedAsCanceled()
    {
        using var cancellation = new CancellationTokenSource();
        var runner = new FakeRunner("winget.exe", async token =>
        {
            cancellation.Cancel();
            await Task.Delay(Timeout.Infinite, token);
            return 0;
        });
        Assert.Equal(FFmpegInstallOutcome.Canceled, await new FFmpegSetup(runner, () => null).InstallAsync(cancellation.Token));
    }
}
