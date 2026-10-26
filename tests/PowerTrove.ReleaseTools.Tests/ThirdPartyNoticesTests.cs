using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using Xunit;

namespace PowerTrove.ReleaseTools.Tests;

public sealed class ThirdPartyNoticesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "powertrove-notices-" + Guid.NewGuid().ToString("N"));
    private string Repository => Path.Combine(_root, "repo");
    private string Stage => Path.Combine(_root, "stage");
    private string Package => Path.Combine(_root, "packages", "supplier.fixture", "1.2.3");

    private void Fixture(string? declared = "LICENSE.txt")
    {
        Directory.CreateDirectory(Package);
        File.WriteAllText(Path.Combine(Package, "LICENSE.txt"), "Supplier copyright and complete synthetic terms.\r\n");
        File.WriteAllText(Path.Combine(Package, "NOTICE.txt"), "Synthetic attribution.\r\n");
        new XDocument(new XElement("package", new XElement("metadata",
            new XElement("id", "Supplier.Fixture"), new XElement("version", "1.2.3"),
            declared is null ? null : new XElement("license", new XAttribute("type", "file"), declared))))
            .Save(Path.Combine(Package, "supplier.fixture.nuspec"));
        foreach (string project in new[] { "PowerTrove.Desktop", "PowerTrove.PackHost" })
        {
            string obj = Path.Combine(Repository, "src", project, "obj");
            Directory.CreateDirectory(obj);
            File.WriteAllText(Path.Combine(obj, "project.assets.json"), JsonSerializer.Serialize(new
            {
                packageFolders = new Dictionary<string, object> { [Path.Combine(_root, "packages")] = new { } },
                libraries = new { }
            }));
            string output = project == "PowerTrove.Desktop" ? Stage : Path.Combine(Stage, "packhost");
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, project == "PowerTrove.Desktop" ? "PowerTrove.deps.json" : "PowerTrove.PackHost.deps.json"),
                JsonSerializer.Serialize(new { libraries = new Dictionary<string, object> { ["Supplier.Fixture/1.2.3"] = new { type = "package" } } }));
        }
    }

    [Fact]
    public async Task PreservesSupplierBytesAndRecordsHashesWithoutClaimingClearance()
    {
        Fixture();
        await ThirdPartyNotices.BuildAsync(Repository, Stage);
        byte[] original = File.ReadAllBytes(Path.Combine(Package, "LICENSE.txt"));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(Stage, "licenses", "supplier.fixture", "1.2.3", "LICENSE.txt")));
        string json = File.ReadAllText(Path.Combine(Stage, "licenses", "dependency-inventory.json"));
        using var inventory = JsonDocument.Parse(json);
        Assert.False(inventory.RootElement.GetProperty("RedistributionCleared").GetBoolean());
        Assert.Equal(1, inventory.RootElement.GetProperty("Packages").GetArrayLength());
        Assert.Contains(Convert.ToHexString(SHA256.HashData(original)).ToLowerInvariant(), json);
        Assert.DoesNotContain(_root, json);
        Assert.Contains("licenses/supplier.fixture/1.2.3/NOTICE.txt", File.ReadAllText(Path.Combine(Stage, "THIRD-PARTY-NOTICES.txt")));
    }

    [Fact]
    public async Task SupplierApacheText_DoesNotInheritProjectCopyright()
    {
        Fixture();
        string metadataPath = Path.Combine(Package, "supplier.fixture.nuspec");
        var xml = XDocument.Load(metadataPath);
        xml.Root!.Element("metadata")!.Element("license")!.ReplaceWith(
            new XElement("license", new XAttribute("type", "expression"), "Apache-2.0"));
        xml.Save(metadataPath);
        File.WriteAllText(Path.Combine(Repository, "LICENSE"), "Project-specific copyright header");
        string canonical = Path.Combine(Repository, "packaging", "windows", "licenses", "Apache-2.0.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(canonical)!);
        File.WriteAllText(canonical, "Synthetic generic license fixture without project attribution.");
        await ThirdPartyNotices.BuildAsync(Repository, Stage);
        Assert.Equal(File.ReadAllBytes(canonical), File.ReadAllBytes(
            Path.Combine(Stage, "licenses", "supplier.fixture", "1.2.3", "LICENSE-Apache-2.0.txt")));
    }

    [Fact]
    public async Task MissingTextsRemainBlockedInsteadOfInventingTerms()
    {
        Fixture(null);
        File.Delete(Path.Combine(Package, "LICENSE.txt"));
        File.Delete(Path.Combine(Package, "NOTICE.txt"));
        await ThirdPartyNotices.BuildAsync(Repository, Stage);
        Assert.Contains("Missing legal text; blocked", File.ReadAllText(Path.Combine(Stage, "licenses", "dependency-inventory.json")));
    }

    [Fact]
    public async Task RejectsSupplierLicenseTraversal()
    {
        Fixture("../private.txt");
        await Assert.ThrowsAsync<InvalidDataException>(() => ThirdPartyNotices.BuildAsync(Repository, Stage));
        Assert.False(File.Exists(Path.Combine(Stage, "THIRD-PARTY-NOTICES.txt")));
    }

    [Fact]
    public async Task RejectsExistingOutputAndSupportsCancellation()
    {
        Fixture();
        Directory.CreateDirectory(Path.Combine(Stage, "licenses"));
        await Assert.ThrowsAsync<IOException>(() => ThirdPartyNotices.BuildAsync(Repository, Stage));
        Directory.Delete(Path.Combine(Stage, "licenses"));
        File.WriteAllText(Path.Combine(Stage, "THIRD-PARTY-NOTICES.txt"), "existing supplier notice");
        await Assert.ThrowsAsync<IOException>(() => ThirdPartyNotices.BuildAsync(Repository, Stage));
        Assert.Equal("existing supplier notice", File.ReadAllText(Path.Combine(Stage, "THIRD-PARTY-NOTICES.txt")));
        File.Delete(Path.Combine(Stage, "THIRD-PARTY-NOTICES.txt"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ThirdPartyNotices.BuildAsync(Repository, Stage, new CancellationToken(true)));
    }

    private string SupplementalFixture(string? failure = null)
    {
        Fixture(null);
        File.Delete(Path.Combine(Package, "LICENSE.txt"));
        File.Delete(Path.Combine(Package, "NOTICE.txt"));
        string metadataPath = Path.Combine(Package, "supplier.fixture.nuspec");
        var xml = XDocument.Load(metadataPath);
        xml.Root!.Element("metadata")!.Add(new XElement("licenseUrl", "https://supplier.example/license"));
        xml.Save(metadataPath);
        string relative = "packaging/windows/licenses/fixture.rtf";
        string source = Path.Combine(Repository, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, "{\\rtf1 Synthetic supplier terms.}");
        File.WriteAllText(Path.Combine(Repository, "packaging", "windows", "license-overrides.json"), JsonSerializer.Serialize(new
        {
            SchemaVersion = 1,
            Overrides = new[] { new { Package = "Supplier.Fixture", Version = "1.2.3", LicenseUrl = "https://supplier.example/license",
                DownloadUrl = "https://supplier.example/download/terms.rtf", SourceFile = failure == "path" ? "../outside.rtf" : relative,
                Sha256 = failure == "hash" ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))).ToLowerInvariant() } }
        }));
        return source;
    }

    [Fact]
    public async Task PreservesPinnedSupplierSnapshotAndPublicProvenance()
    {
        string source = SupplementalFixture();
        await ThirdPartyNotices.BuildAsync(Repository, Stage);
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(Path.Combine(Stage, "licenses", "supplier.fixture", "1.2.3", "SUPPLEMENTAL-LICENSE.rtf")));
        string json = File.ReadAllText(Path.Combine(Stage, "licenses", "dependency-inventory.json"));
        Assert.Contains("https://supplier.example/download/terms.rtf", json);
        Assert.DoesNotContain("Missing legal text", json);
        Assert.Contains("review pending", json);
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("path")]
    public async Task RejectsMismatchedOrEscapingSupplementalText(string failure)
    {
        SupplementalFixture(failure);
        await Assert.ThrowsAsync<InvalidDataException>(() => ThirdPartyNotices.BuildAsync(Repository, Stage));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
