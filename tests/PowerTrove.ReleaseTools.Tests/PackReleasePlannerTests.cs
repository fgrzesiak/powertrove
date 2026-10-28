using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace PowerTrove.ReleaseTools.Tests;

public sealed class PackReleasePlannerTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "PowerTrove-pack-plan-test-" + Guid.NewGuid().ToString("N"));
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private string Root => Path.Combine(_base, "repo");
    private readonly Runner _runner = new();
    public PackReleasePlannerTests()
    {
        Write("src/Sdk/Sdk.csproj", "<Project />"); Write("src/Sdk/Sdk.cs", "// generated SDK contract fixture");
        Write("tools/Imaging/Imaging.csproj", "<Project />"); Write("tools/Imaging/Image.cs", "// generated engine fixture");
        Write("packs/Images/PowerTrove.Pack.Images.csproj", "<Project><ItemGroup><ProjectReference Include=\"../../src/Sdk/Sdk.csproj\"/><ProjectReference Include=\"../../tools/Imaging/Imaging.csproj\"/></ItemGroup></Project>");
        Write("packs/Images/Action.cs", "// generated image action fixture");
        Write("packs/Files/PowerTrove.Pack.Files.csproj", "<Project><ItemGroup><ProjectReference Include=\"../../src/Sdk/Sdk.csproj\"/></ItemGroup></Project>");
        Write("packs/Files/Action.cs", "// generated file action fixture");
        foreach (string folder in new[] { "src/Sdk", "tools/Imaging", "packs/Images", "packs/Files" })
            Write(folder + "/obj/project.assets.json", "{\"libraries\":{}}");
        Write("pack-releases.json", JsonSerializer.Serialize(new
        {
            schemaVersion = 1, runtime = "win-x64", packs = new[] {
                new { id = "powertrove.images", assemblyName = "PowerTrove.Pack.Images", project = "packs/Images/PowerTrove.Pack.Images.csproj", initialVersion = "0.1.0", minimumHostVersion = "0.1.0" },
                new { id = "powertrove.files", assemblyName = "PowerTrove.Pack.Files", project = "packs/Files/PowerTrove.Pack.Files.csproj", initialVersion = "0.1.0", minimumHostVersion = "0.1.0" }
            }
        }));
        Write("src/PowerTrove.Application/Resources/PackDistribution.json", JsonSerializer.Serialize(new { TrustedKeys = new[] { Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo()) } }));
        _runner.Root = Root;
    }
    [Fact]
    public async Task HostOnlyChangesAndSdkPatchDoNotReleaseUnchangedPacks()
    {
        var first = await Plan(); string previous = Catalog(first);
        Write("src/Desktop/App.cs", "// host-only change");
        var next = await Plan(previous, "10.0.200");
        Assert.All(next.Packs, p => { Assert.False(p.Changed); Assert.Equal("0.1.0", p.Version); });
    }
    [Fact]
    public async Task ReferencedEngineChangeReleasesOnlyDependentPack_AndSdkChangeReleasesBoth()
    {
        string previous = Catalog(await Plan());
        Write("tools/Imaging/Image.cs", "// changed engine fixture");
        var engine = await Plan(previous);
        Assert.Equal("0.1.1", engine.Packs.Single(p => p.Id == "powertrove.images").Version);
        Assert.False(engine.Packs.Single(p => p.Id == "powertrove.files").Changed);
        Write("src/Sdk/Sdk.cs", "// changed SDK fixture");
        Assert.All((await Plan(previous)).Packs, p => { Assert.True(p.Changed); Assert.Equal("0.1.1", p.Version); });
    }
    [Fact]
    public async Task ResolvedDependencyChangeAndMinimumHostPolicyAreInputs()
    {
        string previous = Catalog(await Plan());
        Write("tools/Imaging/obj/project.assets.json", "{\"libraries\":{\"Generated.Dependency/2.0.0\":{\"type\":\"package\",\"sha512\":\"generated-digest\"}}}");
        var next = await Plan(previous);
        Assert.True(next.Packs.Single(p => p.Id == "powertrove.images").Changed);
        Assert.False(next.Packs.Single(p => p.Id == "powertrove.files").Changed);
        string policy = File.ReadAllText(Path.Combine(Root, "pack-releases.json"));
        Write("pack-releases.json", policy.Replace("\"minimumHostVersion\":\"0.1.0\"", "\"minimumHostVersion\":\"0.2.0\""));
        Assert.All((await Plan(previous)).Packs, p => Assert.Equal("0.2.0", p.MinimumHostVersion));
    }
    [Fact]
    public async Task LinkedPartialFilenameGlobsIncludeTrackedInputsOutsideTheProjectFolder()
    {
        string project = File.ReadAllText(Path.Combine(Root, "packs/Images/PowerTrove.Pack.Images.csproj"));
        Write("packs/Images/PowerTrove.Pack.Images.csproj", project.Replace("</ItemGroup>", "<Compile Include=\"../../shared/Version*.cs\"/></ItemGroup>"));
        Write("shared/VersionInfo.cs", "// generated linked input");
        string previous = Catalog(await Plan());
        Write("shared/VersionInfo.cs", "// changed generated linked input");
        var next = await Plan(previous);
        Assert.True(next.Packs.Single(p => p.Id == "powertrove.images").Changed);
        Assert.False(next.Packs.Single(p => p.Id == "powertrove.files").Changed);
    }
    [Fact]
    public async Task LegacyCatalogMigratesOnce_AndTamperedCatalogFailsClosed()
    {
        var first = await Plan(); string previous = Catalog(first, legacy: true);
        Assert.All((await Plan(previous)).Packs, p => Assert.Equal("0.1.1", p.Version));
        byte[] signature = File.ReadAllBytes(previous + ".sig"); signature[^1] ^= 1; File.WriteAllBytes(previous + ".sig", signature);
        await Assert.ThrowsAsync<InvalidDataException>(() => Plan(previous));
    }
    [Fact]
    public async Task FrozenPlanIsBoundToCandidateBytes_AndPropertiesCannotInjectBuildTasks()
    {
        string output = Path.Combine(_base, "binding"); await PackReleasePlanner.ExecuteAsync(Root, output, null, "10.0.100", _runner);
        string plan = Path.Combine(output, "pack-plan.json"), zip = Path.Combine(_base, "candidate.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            foreach (string name in new[] { "pack-plan.json", "pack-versions.props" }) archive.CreateEntryFromFile(Path.Combine(output, name), "packs/" + name);
        await PackPlanBinding.VerifyCandidateAsync(zip, plan);
        await File.AppendAllTextAsync(plan, " ");
        await Assert.ThrowsAsync<InvalidDataException>(() => PackPlanBinding.VerifyCandidateAsync(zip, plan));
        await File.WriteAllTextAsync(Path.Combine(output, "pack-versions.props"), "<Project><Target Name=\"Unreviewed\"><Exec Command=\"unreviewed\"/></Target></Project>");
        await Assert.ThrowsAsync<InvalidDataException>(() => PackPlanBinding.ReadAsync(plan));
    }
    private Task<PackReleasePlan> Plan(string? previous = null, string sdk = "10.0.100") =>
        PackReleasePlanner.ExecuteAsync(Root, Path.Combine(_base, Guid.NewGuid().ToString("N")), previous, sdk, _runner);
    private string Catalog(PackReleasePlan plan, bool legacy = false)
    {
        string path = Path.Combine(_base, Guid.NewGuid().ToString("N") + ".json");
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = "2", packs = plan.Packs.Select(p => new
        { manifest = new PowerTrove.Abstractions.Manifest.ActionManifest("2.0", p.Id, p.Version, "Generated fixture", "PowerTrove tests", "1", [], MinHostVersion: p.MinimumHostVersion, Capabilities: [], ReleaseFingerprint: legacy ? null : p.Fingerprint), downloadBytes = 1 }) });
        File.WriteAllBytes(path, bytes); File.WriteAllBytes(path + ".sig", _key.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)); return path;
    }
    private void Write(string relative, string content)
    { string file = Path.Combine(Root, relative); Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, content); }
    private sealed class Runner : IProcessRunner
    {
        public string Root = "";
        public Task<(int ExitCode, string StandardOutput, string StandardError)> RunAsync(string fileName, string arguments, string? workingDirectory = null) =>
            Task.FromResult((0, arguments == "rev-parse HEAD" ? new string('a', 40) : string.Join('\0', Directory.GetFiles(Root, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(Root, f).Replace('\\', '/')).Where(f => !f.Contains("/obj/"))) + "\0", ""));
    }
    public void Dispose() { _key.Dispose(); if (Directory.Exists(_base)) Directory.Delete(_base, true); }
}
