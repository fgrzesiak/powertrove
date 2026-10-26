using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ContextBar.ReleaseTools;

public static class PackageCommand
{
    public static async Task ExecuteAsync(string repoRoot, string outputDir, string version, IProcessRunner processRunner, int? alphaSequence = null)
    {
        if (version is null || version.Length > 64 ||
            !Regex.IsMatch(version, @"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z"))
            throw new ArgumentException("A safe three-part numeric release version is required.", nameof(version));
        if (alphaSequence is < 1 or > 999999)
            throw new ArgumentOutOfRangeException(nameof(alphaSequence), "Alpha sequence must be between 1 and 999999.");
        string releaseVersion = alphaSequence is { } sequence ? $"{version}-alpha.{sequence}" : version;
        repoRoot = Path.GetFullPath(repoRoot);
        outputDir = Path.GetFullPath(outputDir);
        RejectReparseAncestors(outputDir);
        Console.WriteLine("Preparing local PowerTrove package.");

        var desktopProject = Path.Combine(repoRoot, "src", "ContextBar.Desktop", "ContextBar.Desktop.csproj");
        if (!File.Exists(desktopProject))
        {
            throw new FileNotFoundException($"Desktop project not found at {desktopProject}");
        }

        var workerProject = Path.Combine(repoRoot, "src", "ContextBar.PackHost", "ContextBar.PackHost.csproj");
        if (!File.Exists(workerProject))
            throw new FileNotFoundException("PackHost project is missing.");

        var stagingDir = Path.Combine(outputDir, $"PowerTrove-v{version}-win-x64");
        var zipFilePath = Path.Combine(outputDir, $"PowerTrove-v{version}-win-x64.zip");
        // A release command is not authorized to delete existing user files or artifacts.
        if (Directory.Exists(stagingDir) || File.Exists(stagingDir) ||
            File.Exists(zipFilePath) || File.Exists(Path.Combine(outputDir, "SHA256SUMS")))
            throw new IOException("Release output already exists. Use a fresh directory.");

        // Check the exact source revision before touching the output directory.
        var gitRevision = await GetGitRevisionAsync(repoRoot, processRunner);
        RequireNotices(repoRoot);
        Directory.CreateDirectory(outputDir);
        Directory.CreateDirectory(stagingDir);
        Console.WriteLine($"Git Revision: {gitRevision}");

        // Step 2: Publish Desktop Project
        Console.WriteLine("Publishing Desktop project...");
        await PublishProjectAsync(desktopProject, stagingDir, version, releaseVersion, gitRevision, processRunner);
        // The standalone worker is a runtime dependency of signed and built-in packs.
        string workerDirectory = Path.Combine(stagingDir, "packhost");
        Directory.CreateDirectory(workerDirectory);
        await PublishProjectAsync(workerProject, workerDirectory, version, releaseVersion, gitRevision, processRunner);
        RejectReparseAncestors(stagingDir);
        if (!File.Exists(Path.Combine(workerDirectory, "ContextBar.PackHost.exe")))
            throw new InvalidDataException("Published worker executable is missing.");

        // Step 3: Copy root notices
        Console.WriteLine("Copying license and notices...");
        CopyRootNotices(repoRoot, stagingDir);

        // Step 4: Validate Payload and Filter Unsafe Assets
        Console.WriteLine("Validating payload and checking for unsafe assets...");
        ValidateAndCleanPayload(stagingDir);

        // Step 5: Generate Manifest
        Console.WriteLine("Generating manifest with component inventory...");
        await GenerateManifestAsync(stagingDir, version, releaseVersion, gitRevision);

        // Step 6: Create ZIP
        Console.WriteLine("Creating ZIP archive...");
        ZipFile.CreateFromDirectory(stagingDir, zipFilePath);

        // Step 7: Generate SHA256 sums
        Console.WriteLine("Generating SHA256 sums...");
        await GenerateSha256SumsAsync(outputDir, $"PowerTrove-v{version}-win-x64.zip");

        Console.WriteLine("Packaging complete.");
    }

    private static async Task<string> GetGitRevisionAsync(string repoRoot, IProcessRunner processRunner)
    {
        // First check if repository is clean
        var statusResult = await processRunner.RunAsync("git", "status --porcelain", repoRoot);
        if (statusResult.ExitCode != 0 || !string.IsNullOrWhiteSpace(statusResult.StandardOutput))
        {
            throw new Exception("Git repository is dirty or ambiguous. Cannot package a release from a dirty working tree.");
        }

        var result = await processRunner.RunAsync("git", "rev-parse HEAD", repoRoot);
        string sha = result.StandardOutput.Trim();
        if (result.ExitCode != 0 || sha.Length != 40 || !sha.All(Uri.IsHexDigit))
            throw new InvalidDataException("Unable to verify an exact 40-character Git revision.");
        return sha;
    }

    private static async Task PublishProjectAsync(string projectPath, string outputDir, string version, string releaseVersion,
        string revision, IProcessRunner processRunner)
    {
        var result = await processRunner.RunAsync("dotnet", $"publish \"{projectPath}\" -c Release -r win-x64 --self-contained true -p:Version={version} -p:InformationalVersion={releaseVersion} -p:SourceRevisionId={revision} -p:PublishSingleFile=false -p:WindowsAppSDKSelfContained=true -o \"{outputDir}\"");

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"dotnet publish failed with exit code {result.ExitCode}.");
        }
    }

    private static void CopyRootNotices(string repoRoot, string stagingDir)
    {
        var licensePath = Path.Combine(repoRoot, "LICENSE");
        var noticePath = Path.Combine(repoRoot, "NOTICE");

        File.Copy(licensePath, Path.Combine(stagingDir, "LICENSE"));
        File.Copy(noticePath, Path.Combine(stagingDir, "NOTICE"));
        File.Copy(Path.Combine(repoRoot, "packaging", "windows", "QUICKSTART.md"), Path.Combine(stagingDir, "QUICKSTART.md"));
    }

    private static void RequireNotices(string root)
    {
        foreach (string name in new[] { "LICENSE", "NOTICE" })
        {
            var info = new FileInfo(Path.Combine(root, name));
            if (!info.Exists || info.Length == 0 || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Release requires nonempty, regular license and notice files.");
        }
        var quickstart = new FileInfo(Path.Combine(root, "packaging", "windows", "QUICKSTART.md"));
        if (!quickstart.Exists || quickstart.Length == 0 || (quickstart.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Release requires a regular offline quickstart.");
    }

    private static void RejectReparseAncestors(string path)
    {
        for (DirectoryInfo? current = new(Path.GetFullPath(path)); current is not null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Release directories must not traverse reparse points.");
    }

    private static IEnumerable<string> EnumerateSafeFiles(string root)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(root))
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Release payload contains a linked file or directory.");
            if ((attributes & FileAttributes.Directory) != 0)
            {
                foreach (string nested in EnumerateSafeFiles(path)) yield return nested;
            }
            else yield return path;
        }
    }

    private static void ValidateAndCleanPayload(string stagingDir)
    {
        foreach (string path in EnumerateSafeFiles(stagingDir))
        {
            string name = Path.GetFileName(path);
            if (name.Equals("appsettings.Development.json", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("secrets.json", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith(".env", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".pem", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".key", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Release contains a restricted configuration or key file.");
            if (name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                File.Delete(path);
        }
    }

    private static async Task GenerateManifestAsync(string stagingDir, string version, string releaseVersion, string gitRevision)
    {
        var components = new List<object>();
        if (File.Exists(Path.Combine(stagingDir, "manifest.json")))
            throw new InvalidDataException("Published payload must not provide a release manifest.");

        var executableFiles = EnumerateSafeFiles(stagingDir)
            .Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

        foreach (var file in executableFiles)
        {
            var info = FileVersionInfo.GetVersionInfo(file);
            await using var data = File.OpenRead(file);
            components.Add(new
            {
                FileName = Path.GetFileName(file),
                RelativePath = Path.GetRelativePath(stagingDir, file).Replace('\\', '/'),
                SizeBytes = data.Length,
                Sha256 = Convert.ToHexString(await SHA256.HashDataAsync(data)).ToLowerInvariant(),
                ProductName = info.ProductName ?? "",
                FileVersion = info.FileVersion ?? "",
                CompanyName = info.CompanyName ?? ""
            });
        }

        var manifest = new
        {
            Version = version,
            ReleaseVersion = releaseVersion,
            GitRevision = gitRevision,
            Date = DateTime.UtcNow.ToString("O"),
            Target = "win-x64",
            PublicReady = false,
            WindowsAcceptance = "Not run",
            Signing = "Blocked",
            Components = components
        };

        var manifestJson = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Combine(stagingDir, "manifest.json"), manifestJson);
    }

    private static async Task GenerateSha256SumsAsync(string outputDir, string zipFileName)
    {
        var zipFilePath = Path.Combine(outputDir, zipFileName);
        using var sha256 = SHA256.Create();
        await using var stream = File.OpenRead(zipFilePath);
        var hashBytes = await sha256.ComputeHashAsync(stream);
        var hashString = Convert.ToHexString(hashBytes).ToLowerInvariant();

        var sumsContent = $"{hashString} *{zipFileName}\n";
        await File.WriteAllTextAsync(Path.Combine(outputDir, "SHA256SUMS"), sumsContent);
    }
}
