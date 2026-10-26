using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace PowerTrove.ReleaseTools.Tests;

public sealed class InstallerBuildCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PowerTrove-Installer-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Build_WritesInstallerChecksumWithoutLosingZipChecksum()
    {
        var (_, zip, artifacts) = Fixture();
        string compiler = CompilerFixture();
        string installer = Path.Combine(artifacts, "PowerTrove-v1.0.0-win-x64-installer.exe");
        var runner = new Fake { OnRun = () => File.WriteAllText(installer, "compiled installer") };
        await InstallerBuildCommand.ExecuteAsync(_root, artifacts, "1.0.0", compiler, runner);
        string[] lines = File.ReadAllLines(Path.Combine(artifacts, "SHA256SUMS"));
        Assert.Equal(2, lines.Length);
        foreach (string file in new[] { zip, installer })
        {
            string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant();
            Assert.Contains(hash + " *" + Path.GetFileName(file), lines);
        }
    }

    [Fact]
    public async Task Build_RefusesExistingInstallerBeforeInvokingCompiler()
    {
        var (_, _, artifacts) = Fixture();
        string compiler = CompilerFixture();
        string installer = Path.Combine(artifacts, "PowerTrove-v1.0.0-win-x64-installer.exe");
        File.WriteAllText(installer, "previous candidate");
        var runner = new Fake();
        await Assert.ThrowsAsync<IOException>(() =>
            InstallerBuildCommand.ExecuteAsync(_root, artifacts, "1.0.0", compiler, runner));
        Assert.False(runner.WasInvoked);
        Assert.Equal("previous candidate", File.ReadAllText(installer));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    public async Task Build_CompilerFailureOrMissingOutputDoesNotAddChecksum(int exitCode)
    {
        var (_, _, artifacts) = Fixture();
        string compiler = CompilerFixture();
        string sums = File.ReadAllText(Path.Combine(artifacts, "SHA256SUMS"));
        await Assert.ThrowsAnyAsync<Exception>(() => InstallerBuildCommand.ExecuteAsync(
            _root, artifacts, "1.0.0", compiler, new Fake { ExitCode = exitCode }));
        Assert.Equal(sums, File.ReadAllText(Path.Combine(artifacts, "SHA256SUMS")));
    }

    private string CompilerFixture()
    {
        Directory.CreateDirectory(Path.Combine(_root, "packaging", "windows"));
        File.WriteAllText(Path.Combine(_root, "packaging", "windows", "PowerTrove.iss"), "test script");
        string compiler = Path.Combine(_root, "ISCC.exe");
        File.WriteAllText(compiler, "test compiler");
        return compiler;
    }

    [Fact]
    public async Task RefusesMissingCompiler_WithoutInvokingBuild()
    {
        var runner = new Fake();
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            InstallerBuildCommand.ExecuteAsync(_root, _root, "1.0.0", "missing-ISCC.exe", runner));
        Assert.False(runner.WasInvoked);
    }

    [Fact]
    public async Task ExactPayloadValidation_RejectsModifiedStagedBytes()
    {
        var (stage, zip, artifacts) = Fixture();
        File.WriteAllText(Path.Combine(stage, "PowerTrove.exe"), "mutated");
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            InstallerBuildCommand.ValidateExactPayload(stage, zip, "1.0.0", artifacts));
    }

    [Fact]
    public async Task ExactPayloadValidation_RejectsWrongDeclaredVersion()
    {
        var (stage, zip, artifacts) = Fixture();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            InstallerBuildCommand.ValidateExactPayload(stage, zip, "9.9.9", artifacts));
    }

    [Fact]
    public async Task ExactPayloadValidation_AcceptsMatchingStageAndZip()
    {
        var (stage, zip, artifacts) = Fixture();
        await InstallerBuildCommand.ValidateExactPayload(stage, zip, "1.0.0", artifacts);
    }

    [Fact]
    public async Task ExactPayloadValidation_RejectsNestedLinkedDirectory()
    {
        var (stage, zip, artifacts) = Fixture();
        string external = Path.Combine(_root, "outside-stage");
        Directory.CreateDirectory(external);
        File.WriteAllText(Path.Combine(external, "private-fixture.txt"), "not a candidate");
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(stage, "linked"), external);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return; // Symlink creation is unavailable to this test account.
        }
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            InstallerBuildCommand.ValidateExactPayload(stage, zip, "1.0.0", artifacts));
    }

    private (string, string, string) Fixture()
    {
        string artifacts = Path.Combine(_root, "artifacts");
        string stage = Path.Combine(artifacts, "PowerTrove-v1.0.0-win-x64");
        Directory.CreateDirectory(stage);
        File.WriteAllText(Path.Combine(stage, "PowerTrove.exe"), "app");
        File.WriteAllText(Path.Combine(stage, "manifest.json"), JsonSerializer.Serialize(new
        {
            Version = "1.0.0", Target = "win-x64", GitRevision = new string('a', 40)
        }));
        string zip = Path.Combine(artifacts, "PowerTrove-v1.0.0-win-x64.zip");
        ZipFile.CreateFromDirectory(stage, zip);
        string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(zip))).ToLowerInvariant();
        File.WriteAllText(Path.Combine(artifacts, "SHA256SUMS"), hash + " *" + Path.GetFileName(zip));
        return (stage, zip, artifacts);
    }

    private sealed class Fake : IProcessRunner
    {
        public bool WasInvoked;
        public Action? OnRun;
        public int ExitCode;
        public Task<(int ExitCode, string StandardOutput, string StandardError)> RunAsync(
            string filename, string args, string? workingDirectory = null)
        {
            WasInvoked = true;
            OnRun?.Invoke();
            return Task.FromResult((ExitCode, "", ""));
        }
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
        catch (IOException) { }
    }
}
