using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Xml;

namespace PowerTrove.ReleaseTools;

/// <summary>Preserves supplier legal texts from the restored packages used by published executables.
/// This inventory is review material, never a legal clearance or evidence of native-code completeness.</summary>
public static class ThirdPartyNotices
{
    public sealed record PackageNotice(string Package, string Version, string Origin,
        string? LicenseExpression, string? LicenseUrl, string? PackageSha512,
        IReadOnlyList<NoticeFile> Files, string ReviewStatus, string? SupplementalSourceUrl = null);
    public sealed record NoticeFile(string Path, string Sha256);
    public sealed record SupplementalLicense(string Package, string Version, string LicenseUrl,
        string DownloadUrl, string SourceFile, string Sha256);
    private sealed record SupplementalManifest(int SchemaVersion, IReadOnlyList<SupplementalLicense> Overrides);

    public static async Task BuildAsync(string repository, string stage, CancellationToken token = default)
    {
        RequireRegularDirectory(stage);
        string destination = Path.Combine(stage, "licenses");
        if (Directory.Exists(destination) || File.Exists(destination) ||
            File.Exists(Path.Combine(stage, "THIRD-PARTY-NOTICES.txt")))
            throw new IOException("Notice output already exists.");
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var packages = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var supplemental = new Dictionary<string, SupplementalLicense>(StringComparer.OrdinalIgnoreCase);
        string supplementalFile = Path.Combine(repository, "packaging", "windows", "license-overrides.json");
        if (File.Exists(supplementalFile))
        {
            RequireRegularFile(supplementalFile);
            var manifest = JsonSerializer.Deserialize<SupplementalManifest>(await File.ReadAllTextAsync(supplementalFile, token));
            if (manifest is null || manifest.SchemaVersion != 1 || manifest.Overrides is null)
                throw new InvalidDataException("Invalid supplemental supplier license manifest.");
            foreach (var entry in manifest.Overrides)
                if (!supplemental.TryAdd(entry.Package + "/" + entry.Version, entry))
                    throw new InvalidDataException("Duplicate supplemental supplier license.");
        }
        foreach (string project in new[] { "PowerTrove.Desktop", "PowerTrove.PackHost" })
        {
            string assets = Path.Combine(repository, "src", project, "obj", "project.assets.json");
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(assets, token));
            foreach (var root in document.RootElement.GetProperty("packageFolders").EnumerateObject()) roots.Add(root.Name);
            // SDK native assets can be copied by build targets rather than represented in .deps.json.
            foreach (var library in document.RootElement.GetProperty("libraries").EnumerateObject())
                if (library.Name.StartsWith("Microsoft.WindowsAppSDK", StringComparison.OrdinalIgnoreCase) &&
                    library.Value.GetProperty("type").GetString() == "package")
                    packages.TryAdd(library.Name, "SDK dependency; payload membership requires review");
        }
        foreach (string file in new[] { Path.Combine(stage, "PowerTrove.deps.json"),
            Path.Combine(stage, "packhost", "PowerTrove.PackHost.deps.json") })
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(file, token));
            foreach (var library in document.RootElement.GetProperty("libraries").EnumerateObject())
            {
                string? type = library.Value.GetProperty("type").GetString();
                if (type is not ("package" or "runtimepack")) continue;
                string identity = library.Name.StartsWith("runtimepack.", StringComparison.Ordinal)
                    ? library.Name[12..] : library.Name;
                packages[identity] = type == "runtimepack" ? "Published runtime pack" : "Published dependency manifest";
            }
        }
        Directory.CreateDirectory(destination);
        var inventory = new List<PackageNotice>();
        var index = new StringBuilder("Third-party legal texts and dependency review inventory\n\n" +
            "These supplier texts are preserved verbatim. This collection does not grant redistribution clearance.\n" +
            "Native/static dependencies and license obligations require review against the exact final payload.\n" +
            "See licenses/dependency-inventory.json. Missing texts remain explicitly unresolved.\n\n");
        foreach (var (identity, origin) in packages)
        {
            token.ThrowIfCancellationRequested();
            string[] parts = identity.Split('/');
            if (parts.Length != 2 || parts.Any(p => !Regex.IsMatch(p, "^[A-Za-z0-9][A-Za-z0-9_.+-]*$") || p.Contains("..")))
                throw new InvalidDataException("Unsafe dependency identity.");
            string id = parts[0], version = parts[1];
            string relative = Path.Combine(id.ToLowerInvariant(), version.ToLowerInvariant());
            string package = roots.Select(root => Path.Combine(root, relative)).FirstOrDefault(Directory.Exists)
                ?? throw new DirectoryNotFoundException("Restored supplier package is unavailable: " + identity);
            RequireRegularDirectory(package);
            string nuspec = Path.Combine(package, id.ToLowerInvariant() + ".nuspec");
            RequireRegularFile(nuspec);
            using var reader = XmlReader.Create(nuspec, new XmlReaderSettings
                { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024 });
            var xml = XDocument.Load(reader);
            var metadata = xml.Root!.Elements().Single(e => e.Name.LocalName == "metadata");
            var license = metadata.Elements().FirstOrDefault(e => e.Name.LocalName == "license");
            string? expression = (string?)license?.Attribute("type") == "expression" ? license.Value : null;
            string? url = metadata.Elements().FirstOrDefault(e => e.Name.LocalName == "licenseUrl")?.Value;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var licenseUri) || licenseUri.Scheme != "https") url = null;
            var sources = Directory.GetFiles(package).Where(p => IsLegalText(Path.GetFileName(p)))
                .ToDictionary(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase);
            if ((string?)license?.Attribute("type") == "file")
            {
                string declared = license.Value.Replace('\\', '/');
                if (Path.IsPathRooted(declared) || declared.Split('/').Any(p => p is ".." or "." or ""))
                    throw new InvalidDataException("Unsafe supplier license path.");
                sources[declared] = Path.Combine(package, declared.Replace('/', Path.DirectorySeparatorChar));
            }
            // Apache expressions refer to the standard license text, already supplied by this project.
            if (expression == "Apache-2.0") sources.TryAdd("LICENSE-Apache-2.0.txt", Path.Combine(repository, "LICENSE"));
            string? supplementalUrl = null;
            if (supplemental.TryGetValue(identity, out var extra))
            {
                if (extra.LicenseUrl != url || !Uri.TryCreate(extra.DownloadUrl, UriKind.Absolute, out var downloadUri) ||
                    downloadUri.Scheme != "https" || !string.IsNullOrEmpty(downloadUri.UserInfo) ||
                    !Regex.IsMatch(extra.Sha256, "^[a-f0-9]{64}$") ||
                    Path.IsPathRooted(extra.SourceFile) || extra.SourceFile.Replace('\\', '/').Split('/').Any(p => p is ".." or "." or ""))
                    throw new InvalidDataException("Invalid supplemental supplier license provenance.");
                string source = Path.Combine(repository, extra.SourceFile);
                RequireRegularFile(source);
                string hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(source, token))).ToLowerInvariant();
                if (hash != extra.Sha256) throw new InvalidDataException("Supplemental supplier license checksum mismatch.");
                sources.Add("SUPPLEMENTAL-LICENSE" + Path.GetExtension(source), source);
                supplementalUrl = extra.DownloadUrl;
            }
            var notices = new List<NoticeFile>();
            foreach (var (name, source) in sources.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                RequireRegularFile(source);
                string target = Path.Combine(destination, relative, name);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                byte[] bytes = await File.ReadAllBytesAsync(source, token);
                if (bytes.Length == 0) throw new InvalidDataException("Supplier legal text is empty.");
                await File.WriteAllBytesAsync(target, bytes, token);
                notices.Add(new(Path.GetRelativePath(stage, target).Replace('\\', '/'),
                    Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()));
            }
            string hashFile = Path.Combine(package, id.ToLowerInvariant() + "." + version.ToLowerInvariant() + ".nupkg.sha512");
            string? packageHash = null;
            if (File.Exists(hashFile))
            {
                RequireRegularFile(hashFile);
                packageHash = (await File.ReadAllTextAsync(hashFile, token)).Trim();
                if (Convert.FromBase64String(packageHash).Length != 64) throw new InvalidDataException("Invalid supplier package hash.");
            }
            string status = notices.Count == 0 ? "Missing legal text; blocked" : "Supplier texts collected; review pending";
            inventory.Add(new(id, version, origin, expression, url, packageHash, notices, status, supplementalUrl));
            index.AppendLine(identity + " — " + status);
            foreach (var notice in notices) index.AppendLine("  " + notice.Path);
        }
        await File.WriteAllTextAsync(Path.Combine(destination, "dependency-inventory.json"),
            JsonSerializer.Serialize(new { SchemaVersion = 1, RedistributionCleared = false,
                Scope = "Published .NET dependency manifests and supporting Windows App SDK packages; native/static reconciliation remains required",
                Packages = inventory }, new JsonSerializerOptions { WriteIndented = true }), token);
        await File.WriteAllTextAsync(Path.Combine(stage, "THIRD-PARTY-NOTICES.txt"), index.ToString(), token);
    }

    private static bool IsLegalText(string name) => Regex.IsMatch(name,
        "^(license|notice|copyright|copying|third[-_]?party[-_]?notices)([._-].*)?$", RegexOptions.IgnoreCase);

    private static void RequireRegularDirectory(string path)
    {
        for (DirectoryInfo? item = new(path); item is not null; item = item.Parent)
            if (item.Exists && (item.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Supplier directories must not traverse links.");
    }

    private static void RequireRegularFile(string path)
    {
        RequireRegularDirectory(Path.GetDirectoryName(path)!);
        var file = new FileInfo(path);
        if (!file.Exists || (file.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Supplier legal metadata must be a regular file.");
    }
}
