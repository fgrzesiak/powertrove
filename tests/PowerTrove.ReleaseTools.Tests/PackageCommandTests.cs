using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace PowerTrove.ReleaseTools.Tests;

public class PackageCommandTests : IDisposable
{
    private readonly string _testBaseDir;
    private readonly string _repoRoot;
    private readonly string _outputDir;

    public PackageCommandTests()
    {
        _testBaseDir = Path.Combine(Path.GetTempPath(), "PowerTrove.ReleaseTools.Tests", Guid.NewGuid().ToString("N"));
        _repoRoot = Path.Combine(_testBaseDir, "repo");
        _outputDir = Path.Combine(_testBaseDir, "output");

        Directory.CreateDirectory(_repoRoot);
        Directory.CreateDirectory(_outputDir);
    }

    private class FakeProcessRunner : IProcessRunner
    {
        public bool ShouldFailPublish { get; set; }
        public bool IsGitDirty { get; set; }
        public string FakeRevision { get; set; } = new('a', 40);
        public Action<string>? OnPublish { get; set; }
        public List<string> PublishArguments { get; } = [];

        public Task<(int ExitCode, string StandardOutput, string StandardError)> RunAsync(string fileName, string arguments, string? workingDirectory = null)
        {
            if (fileName == "git" && arguments == "status --porcelain")
            {
                if (IsGitDirty) return Task.FromResult((0, " M somefile.txt", ""));
                return Task.FromResult((0, "", ""));
            }

            if (fileName == "git" && arguments == "rev-parse HEAD")
            {
                return Task.FromResult((0, FakeRevision, ""));
            }

            if (fileName == "dotnet" && arguments.StartsWith("publish"))
            {
                PublishArguments.Add(arguments);
                if (ShouldFailPublish) return Task.FromResult((1, "", "Build FAILED."));

                // Simulate publish by invoking callback (e.g. creating files in outputDir)
                // Extract output dir from arguments
                var parts = arguments.Split("-o \"");
                if (parts.Length > 1)
                {
                    var outputDir = parts[1].Split("\"")[0];
                    Directory.CreateDirectory(outputDir);
                    string depsName = arguments.Contains("PowerTrove.PackHost.csproj", StringComparison.Ordinal)
                        ? "PowerTrove.PackHost.deps.json" : "PowerTrove.deps.json";
                    File.WriteAllText(Path.Combine(outputDir, depsName), "{\"libraries\":{}}");
                    if (arguments.Contains("PowerTrove.PackHost.csproj", StringComparison.Ordinal))
                {
                    Directory.CreateDirectory(outputDir);
                    File.WriteAllText(Path.Combine(outputDir, "PowerTrove.PackHost.exe"), "dummy worker");
                }
                else
                {
                    foreach (string name in new[] { "PowerTrove.exe", "PowerTrove.pri", "App.xbf", "MainWindow.xbf",
                        "SettingsWindow.xbf", "PageOrganizerWindow.xbf" })
                        File.WriteAllText(Path.Combine(outputDir, name), "synthetic runtime resource");
                    OnPublish?.Invoke(outputDir);
                }
                }

                return Task.FromResult((0, "Build succeeded.", ""));
            }

            return Task.FromResult((0, "", ""));
        }
    }

    [Fact]
    public async Task ExecuteAsync_RejectsMissingUiResourcesBeforeCreatingZip()
    {
        CreateDummyDesktopProject();
        var runner = new FakeProcessRunner { OnPublish = dir => File.Delete(Path.Combine(dir, "PowerTrove.pri")) };
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            PackageCommand.ExecuteAsync(_repoRoot, _outputDir, "1.0.0", runner));
        Assert.False(File.Exists(Path.Combine(_outputDir, "PowerTrove-v1.0.0-win-x64.zip")));
    }

    [Theory]
    [InlineData("PowerTrove.exe")]
    [InlineData("PowerTrove.pri")]
    [InlineData("App.xbf")]
    [InlineData("MainWindow.xbf")]
    [InlineData("SettingsWindow.xbf")]
    [InlineData("PageOrganizerWindow.xbf")]
    public void ValidateDesktopResources_RejectsMissingOrEmptyFiles(string missing)
    {
        foreach (string name in new[] { "PowerTrove.exe", "PowerTrove.pri", "App.xbf", "MainWindow.xbf",
            "SettingsWindow.xbf", "PageOrganizerWindow.xbf" })
            File.WriteAllText(Path.Combine(_outputDir, name), "resource");
        PackageCommand.ValidateDesktopResources(_outputDir);
        File.WriteAllText(Path.Combine(_outputDir, missing), "");
        Assert.Throws<InvalidDataException>(() => PackageCommand.ValidateDesktopResources(_outputDir));
        File.Delete(Path.Combine(_outputDir, missing));
        Assert.Throws<InvalidDataException>(() => PackageCommand.ValidateDesktopResources(_outputDir));
    }

    [Fact]
    public async Task ExecuteAsync_FailsIfDesktopProjectIsMissing()
    {
        var ex = await Assert.ThrowsAsync<FileNotFoundException>(() => PackageCommand.ExecuteAsync(_repoRoot, _outputDir, "1.0.0", new FakeProcessRunner()));
        Assert.Contains("Desktop project not found", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_FailsIfGitIsDirty()
    {
        CreateDummyDesktopProject();
        var runner = new FakeProcessRunner { IsGitDirty = true };

        var ex = await Assert.ThrowsAsync<Exception>(() => PackageCommand.ExecuteAsync(_repoRoot, _outputDir, "1.0.0", runner));
        Assert.Contains("Git repository is dirty or ambiguous", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_FailsIfPublishFails()
    {
        CreateDummyDesktopProject();
        var runner = new FakeProcessRunner { ShouldFailPublish = true };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => PackageCommand.ExecuteAsync(_repoRoot, _outputDir, "1.0.0", runner));
        Assert.Contains("dotnet publish failed", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_FailsIfUnsafeAssetsPresent()
    {
        CreateDummyDesktopProject();
        var runner = new FakeProcessRunner
        {
            OnPublish = dir =>
            {
                File.WriteAllText(Path.Combine(dir, "appsettings.Development.json"), "{}");
            }
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => PackageCommand.ExecuteAsync(_repoRoot, _outputDir, "1.0.0", runner));
        Assert.Contains("Release contains a restricted configuration", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_CreatesZipAndManifest_AndStripsDevArtifacts()
    {
        CreateDummyDesktopProject();
        await File.WriteAllTextAsync(Path.Combine(_repoRoot, "LICENSE"), "Dummy License");
        await File.WriteAllTextAsync(Path.Combine(_repoRoot, "NOTICE"), "Dummy Notice");

        var runner = new FakeProcessRunner
        {
            FakeRevision = new('b', 40),
            OnPublish = dir =>
            {
                File.WriteAllText(Path.Combine(dir, "PowerTrove.dll"), "dummy binary");
                File.WriteAllText(Path.Combine(dir, "PowerTrove.pdb"), "debug info");
                File.WriteAllText(Path.Combine(dir, "PowerTrove.xml"), "xml docs");
            }
        };

        await PackageCommand.ExecuteAsync(_repoRoot, _outputDir, "1.2.3", runner);

        Assert.Equal(2, runner.PublishArguments.Count);
        Assert.All(runner.PublishArguments, arguments =>
        {
            Assert.Contains("-p:Version=1.2.3", arguments);
            Assert.Contains("-p:InformationalVersion=1.2.3 ", arguments);
            Assert.Contains("-p:SourceRevisionId=" + new string('b', 40), arguments);
        });

        var zipFilePath = Path.Combine(_outputDir, "PowerTrove-v1.2.3-win-x64.zip");
        Assert.True(File.Exists(zipFilePath), "ZIP file should have been created.");

        var sumsFilePath = Path.Combine(_outputDir, "SHA256SUMS");
        Assert.True(File.Exists(sumsFilePath));

        using var zipStream = File.OpenRead(zipFilePath);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);

        var manifestEntry = archive.GetEntry("manifest.json");
        Assert.NotNull(manifestEntry);

        using var manifestStream = manifestEntry.Open();
        var manifest = await JsonSerializer.DeserializeAsync<JsonElement>(manifestStream);
        Assert.Equal("1.2.3", manifest.GetProperty("Version").GetString());
        Assert.Equal("win-x64", manifest.GetProperty("Target").GetString());
        Assert.Equal(new string('b', 40), manifest.GetProperty("GitRevision").GetString());
        Assert.False(manifest.GetProperty("PublicReady").GetBoolean());

        // Assert components array exists
        var components = manifest.GetProperty("Components");
        Assert.Equal(JsonValueKind.Array, components.ValueKind);
        Assert.Equal(3, components.GetArrayLength()); // Desktop EXE/DLL and isolated worker EXE
        Assert.Contains(components.EnumerateArray(), c => c.GetProperty("FileName").GetString() == "PowerTrove.dll" && c.GetProperty("Sha256").GetString()?.Length == 64);
        Assert.Contains(components.EnumerateArray(), c => c.GetProperty("FileName").GetString() == "PowerTrove.PackHost.exe");
        Assert.NotNull(archive.GetEntry("packhost/PowerTrove.PackHost.exe"));

        Assert.NotNull(archive.GetEntry("LICENSE"));
        Assert.NotNull(archive.GetEntry("NOTICE"));
        Assert.NotNull(archive.GetEntry("QUICKSTART.md"));
        Assert.NotNull(archive.GetEntry("THIRD-PARTY-NOTICES.txt"));
        Assert.NotNull(archive.GetEntry("licenses/dependency-inventory.json"));

        // Verify dev artifacts were stripped
        Assert.Null(archive.GetEntry("PowerTrove.pdb"));
        Assert.Null(archive.GetEntry("PowerTrove.xml"));
    }

    [Fact]
    public async Task ExecuteAsync_RefusesPreexistingOutput_WithoutDeletingUserData()
    {
        CreateDummyDesktopProject();
        string candidate = Path.Combine(_outputDir, "PowerTrove-v1.0.0-win-x64");
        Directory.CreateDirectory(candidate);
        string sentinel = Path.Combine(candidate, "keep.txt");
        File.WriteAllText(sentinel, "preserve");
        await Assert.ThrowsAsync<IOException>(() =>
            PackageCommand.ExecuteAsync(_repoRoot, _outputDir, "1.0.0", new FakeProcessRunner()));
        Assert.Equal("preserve", File.ReadAllText(sentinel));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("0.1")]
    [InlineData("1.0.0/evil")]
    public async Task ExecuteAsync_RejectsUnsafeVersion(string version)
    {
        CreateDummyDesktopProject();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            PackageCommand.ExecuteAsync(_repoRoot, _outputDir, version, new FakeProcessRunner()));
    }

    [Fact]
    public async Task ExecuteAsync_RejectsUnverifiableGitRevision()
    {
        CreateDummyDesktopProject();
        var runner = new FakeProcessRunner { FakeRevision = "unknown" };
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            PackageCommand.ExecuteAsync(_repoRoot, _outputDir, "1.0.0", runner));
        Assert.Empty(Directory.GetFileSystemEntries(_outputDir));
    }

    [Fact]
    public async Task ExecuteAsync_RejectsMissingLegalNoticesBeforeStaging()
    {
        CreateDummyDesktopProject();
        File.Delete(Path.Combine(_repoRoot, "NOTICE"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PackageCommand.ExecuteAsync(_repoRoot, _outputDir, "1.0.0", new FakeProcessRunner()));
        Assert.Empty(Directory.GetFileSystemEntries(_outputDir));
    }

    [Fact]
    public async Task ExecuteAsync_RejectsMissingOfflineQuickstartBeforeStaging()
    {
        CreateDummyDesktopProject();
        File.Delete(Path.Combine(_repoRoot, "packaging", "windows", "QUICKSTART.md"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PackageCommand.ExecuteAsync(_repoRoot, _outputDir, "1.0.0", new FakeProcessRunner()));
        Assert.Empty(Directory.GetFileSystemEntries(_outputDir));
    }

    [Fact]
    public async Task ExecuteAsync_AlphaChannelReachesBothBinariesAndHashedManifest()
    {
        CreateDummyDesktopProject();
        var runner = new FakeProcessRunner();
        await PackageCommand.ExecuteAsync(_repoRoot, _outputDir, "0.1.0", runner, alphaSequence: 12);
        Assert.Equal(2, runner.PublishArguments.Count);
        Assert.All(runner.PublishArguments, arguments =>
        {
            Assert.Contains("-p:Version=0.1.0 ", arguments);
            Assert.Contains("-p:InformationalVersion=0.1.0-alpha.12 ", arguments);
        });
        using var archive = ZipFile.OpenRead(Path.Combine(_outputDir, "PowerTrove-v0.1.0-win-x64.zip"));
        using var stream = archive.GetEntry("manifest.json")!.Open();
        using var manifest = await JsonDocument.ParseAsync(stream);
        Assert.Equal("0.1.0", manifest.RootElement.GetProperty("Version").GetString());
        Assert.Equal("0.1.0-alpha.12", manifest.RootElement.GetProperty("ReleaseVersion").GetString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1000000)]
    public async Task ExecuteAsync_RejectsInvalidAlphaSequenceBeforeWriting(int sequence)
    {
        var runner = new FakeProcessRunner();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            PackageCommand.ExecuteAsync(_repoRoot, _outputDir, "0.1.0", runner, sequence));
        Assert.Empty(runner.PublishArguments);
        Assert.Empty(Directory.GetFileSystemEntries(_outputDir));
    }

    private void CreateDummyDesktopProject()
    {
        var desktopProjectDir = Path.Combine(_repoRoot, "src", "PowerTrove.Desktop");
        Directory.CreateDirectory(desktopProjectDir);
        File.WriteAllText(Path.Combine(desktopProjectDir, "PowerTrove.Desktop.csproj"), "<Project></Project>");
        var workerDir = Path.Combine(_repoRoot, "src", "PowerTrove.PackHost");
        Directory.CreateDirectory(workerDir);
        File.WriteAllText(Path.Combine(workerDir, "PowerTrove.PackHost.csproj"), "<Project></Project>");
        string packages = Path.Combine(_testBaseDir, "packages");
        Directory.CreateDirectory(packages);
        foreach (string project in new[] { desktopProjectDir, workerDir })
        {
            Directory.CreateDirectory(Path.Combine(project, "obj"));
            File.WriteAllText(Path.Combine(project, "obj", "project.assets.json"), JsonSerializer.Serialize(new
            {
                packageFolders = new Dictionary<string, object> { [packages] = new { } }, libraries = new { }
            }));
        }
        File.WriteAllText(Path.Combine(_repoRoot, "LICENSE"), "Test fixture license");
        File.WriteAllText(Path.Combine(_repoRoot, "NOTICE"), "Test fixture notices");
        Directory.CreateDirectory(Path.Combine(_repoRoot, "packaging", "windows"));
        File.WriteAllText(Path.Combine(_repoRoot, "packaging", "windows", "QUICKSTART.md"), "Offline startup instructions");
    }

    public void Dispose()
    {
        if (Directory.Exists(_testBaseDir))
        {
            try
            {
                Directory.Delete(_testBaseDir, true);
            }
            catch
            {
                // Ignore cleanup failures in tests
            }
        }
    }
}
