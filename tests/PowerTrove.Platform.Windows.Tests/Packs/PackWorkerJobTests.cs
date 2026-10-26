using System.Diagnostics;
using System.Reflection;
using PowerTrove.Application.Packs;
using PowerTrove.FakePackWorker;
using PowerTrove.Platform.Windows.Packs;

namespace PowerTrove.Platform.Windows.Tests.Packs;

public sealed class PackWorkerJobTests
{
    [Fact]
    public async Task DisposingSupervisorClosesTheJobAndKillsTheFixtureWorker()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = new TempDirectory();
        string executable = FixtureExecutable();
        Assert.True(File.Exists(executable), "Build the solution, including the fake worker apphost, before this Windows check.");
        string pack = Directory.CreateDirectory(temp.File("success")).FullName;
        string root = Directory.CreateDirectory(temp.File("user")).FullName;
        var host = new PackHostSupervisor(new WindowsPackWorkerProcessFactory(executable), root);
        try
        {
            string input = temp.Write("source.txt", "original");
            var group = new PackWorkerGroup("fixture", [pack], [new("contextbar.fixture", "0.1.0")]);
            var preview = await host.PreviewAsync(group, "contextbar.fixture.copy", Snapshots.ForPaths(input), new EmptyParameters(), default);
            Assert.False(preview.IsBlocked);
            string pidPath = Assert.Single(Directory.GetFiles(temp.File("user/workers"), "process-id.txt", SearchOption.AllDirectories));
            using var process = Process.GetProcessById(int.Parse(File.ReadAllText(pidPath), System.Globalization.CultureInfo.InvariantCulture));
            Assert.False(process.HasExited);
            await host.DisposeAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(process.HasExited);
            Assert.Empty(Directory.EnumerateFileSystemEntries(temp.File("user/workers")));
        }
        finally { await host.DisposeAsync(); }
    }
    private static string FixtureExecutable()
    {
        string copied = Path.ChangeExtension(typeof(Marker).Assembly.Location, ".exe");
        if (File.Exists(copied)) return copied;
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "PowerTrove.slnx"))) root = root.Parent;
        string configuration = typeof(PackWorkerJobTests).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "Debug";
        return Path.Combine(root?.FullName ?? throw new InvalidOperationException("Repository root not found."),
            "tests", "PowerTrove.FakePackWorker", "bin", configuration, "net10.0", "PowerTrove.FakePackWorker.exe");
    }
}
