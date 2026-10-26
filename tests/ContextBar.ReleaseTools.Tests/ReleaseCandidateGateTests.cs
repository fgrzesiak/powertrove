using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace ContextBar.ReleaseTools.Tests;

public sealed class ReleaseCandidateGateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ContextBar-Gate-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task MissingEvidence_ProducesNotRunAndBlockedReadiness()
    {
        string zip = MakeCandidate();
        var report = await ReleaseCandidateGate.VerifyAsync(zip, Path.Combine(_root, "missing.json"), Path.Combine(_root, "out"));
        Assert.Equal("1.0.0", report.ReleaseVersion);
        Assert.False(report.MandatoryChecksPass);
        Assert.False(report.PublicReady);
        Assert.All(report.Checks, x => Assert.Equal("Not run", x.Status));
        Assert.True(File.Exists(Path.Combine(_root, "out", "release-readiness.json")));
        Assert.True(File.Exists(Path.Combine(_root, "out", "release-readiness.md")));
    }

    [Fact]
    public async Task WrongCandidateEvidence_CannotBeReused()
    {
        string zip = MakeCandidate();
        string evidence = Path.Combine(_root, "evidence.json");
        File.WriteAllText(evidence, JsonSerializer.Serialize(new
        {
            revision = new string('b', 40), payloadSha256 = new string('c', 64), version = "1.0.0",
            checks = new[] { new { id = "offline-core", status = "Pass" } }
        }));
        var report = await ReleaseCandidateGate.VerifyAsync(zip, evidence, Path.Combine(_root, "out"));
        Assert.Contains(report.Checks, x => x.Id == "offline-core" && x.Status != "Pass");
    }

    [Fact]
    public async Task ReplacedZip_FailsChecksumBeforeReportingSuccess()
    {
        string zip = MakeCandidate();
        File.AppendAllText(zip, "corruption");
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ReleaseCandidateGate.VerifyAsync(zip, "absent", Path.Combine(_root, "out")));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1024 * 1024 + 1)]
    public async Task InvalidEvidenceSize_BlocksReadiness(int size)
    {
        string zip = MakeCandidate();
        string evidence = Path.Combine(_root, "evidence.json");
        await File.WriteAllBytesAsync(evidence, new byte[size]);

        var report = await ReleaseCandidateGate.VerifyAsync(zip, evidence, Path.Combine(_root, "out"));

        Assert.False(report.MandatoryChecksPass);
        Assert.False(report.PublicReady);
        Assert.DoesNotContain(report.Checks, check => check.Status == "Pass");
    }

    [Theory]
    [InlineData("1.0.0-alpha.12")]
    [InlineData("1.0.0")]
    public async Task ReleaseChannel_IsReadFromHashedPayload(string releaseVersion)
    {
        string zip = MakeCandidate(releaseVersion);
        var report = await ReleaseCandidateGate.VerifyAsync(zip, "absent", Path.Combine(_root, "out"));
        Assert.Equal(releaseVersion, report.ReleaseVersion);
        Assert.False(report.PublicReady);
    }

    [Theory]
    [InlineData("2.0.0-alpha.1")]
    [InlineData("1.0.0-alpha.0")]
    [InlineData("1.0.0-alpha.01")]
    public async Task InvalidReleaseChannel_IsRejected(string releaseVersion)
    {
        string zip = MakeCandidate(releaseVersion);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ReleaseCandidateGate.VerifyAsync(zip, "absent", Path.Combine(_root, "out")));
    }

    private string MakeCandidate(string? releaseVersion = null)
    {
        Directory.CreateDirectory(_root);
        string zip = Path.Combine(_root, "PowerTrove-v1.0.0-win-x64.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("manifest.json");
            using var writer = new StreamWriter(entry.Open());
            writer.Write(JsonSerializer.Serialize(new
            {
                Version = "1.0.0", ReleaseVersion = releaseVersion, GitRevision = new string('a', 40), Target = "win-x64",
                Date = DateTimeOffset.UtcNow.AddMinutes(-10).ToString("O"),
                Components = new[] { new { FileName = "PowerTrove.exe" } }
            }, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }));
        }
        string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(zip))).ToLowerInvariant();
        File.WriteAllText(Path.Combine(_root, "SHA256SUMS"), hash + " *" + Path.GetFileName(zip));
        return zip;
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
        catch (IOException) { }
    }
}
