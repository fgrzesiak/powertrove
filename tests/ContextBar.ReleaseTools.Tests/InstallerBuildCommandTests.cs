using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace ContextBar.ReleaseTools.Tests;

public sealed class InstallerBuildCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ContextBar-Installer-" + Guid.NewGuid().ToString("N"));

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
        public Task<(int ExitCode, string StandardOutput, string StandardError)> RunAsync(
            string filename, string args, string? workingDirectory = null)
        {
            WasInvoked = true;
            return Task.FromResult((0, "", ""));
        }
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
        catch (IOException) { }
    }
}
