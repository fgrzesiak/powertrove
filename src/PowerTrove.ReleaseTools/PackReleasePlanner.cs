using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using PowerTrove.Abstractions.Manifest;

namespace PowerTrove.ReleaseTools;

public sealed record PackReleasePolicy(int SchemaVersion, string Runtime, PackReleasePolicyItem[] Packs, string ToolchainRevision = "dotnet10-v1");
public sealed record PackReleasePolicyItem(string Id, string AssemblyName, string Project, string InitialVersion, string MinimumHostVersion);
public sealed record PlannedPack(string Id, string AssemblyName, string Project, string Version, string MinimumHostVersion,
    string Fingerprint, bool Changed, string? PreviousVersion);
public sealed record PackReleasePlan(int SchemaVersion, string SourceRevision, string SdkVersion, string Runtime,
    string? BaselineCatalogSha256, PlannedPack[] Packs);

/// <summary>Version planning requires public verification material only, never the private signer.</summary>
public static class PackReleasePlanner
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public static async Task<PackReleasePlan> ExecuteAsync(string root, string output, string? previousCatalog,
        string sdkVersion, IProcessRunner runner, CancellationToken token = default)
    {
        root = Path.GetFullPath(root); output = Path.GetFullPath(output);
        if (Directory.Exists(output) || File.Exists(output)) throw new IOException("Use a new pack plan directory.");
        var revision = await runner.RunAsync("git", "rev-parse HEAD", root);
        var tracked = await runner.RunAsync("git", "ls-files -z", root);
        if (revision.ExitCode != 0 || tracked.ExitCode != 0 || revision.StandardOutput.Trim().Length != 40)
            throw new InvalidDataException("Cannot identify pack source inputs.");
        var policy = JsonSerializer.Deserialize<PackReleasePolicy>(await File.ReadAllTextAsync(Path.Combine(root, "pack-releases.json"), token), Json)
            ?? throw new InvalidDataException("Missing pack release policy.");
        if (policy.SchemaVersion != 1 || policy.Runtime != "win-x64" || policy.Packs.Length is < 1 or > 64 ||
            policy.Packs.Select(p => p.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != policy.Packs.Length)
            throw new InvalidDataException("Invalid pack release policy.");
        var files = tracked.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Replace('\\', '/')).ToHashSet(StringComparer.Ordinal);
        byte[]? baseline = previousCatalog is null ? null : await ReadBoundedAsync(previousCatalog, token);
        var previous = baseline is null ? [] : await ReadPreviousAsync(root, previousCatalog!, baseline, token);
        var planned = new List<PlannedPack>();
        foreach (var pack in policy.Packs)
        {
            token.ThrowIfCancellationRequested();
            if (!SafeId(pack.Id) || !SafeId(pack.AssemblyName) || !Version.TryParse(pack.InitialVersion, out _) || !Version.TryParse(pack.MinimumHostVersion, out _))
                throw new InvalidDataException("Invalid pack release identity.");
            var inputs = new SortedSet<string>(StringComparer.Ordinal);
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var packages = new SortedSet<string>(StringComparer.Ordinal);
            Visit(ProjectPath(root, pack.Project));
            foreach (var file in new[] { "LICENSE", "NOTICE", "global.json", "NuGet.Config", "nuget.config", "Directory.Packages.props",
                "packaging/windows/license-overrides.json" }) if (files.Contains(file)) inputs.Add(file);
            foreach (string file in files.Where(f => f.StartsWith("packaging/windows/licenses/", StringComparison.Ordinal))) inputs.Add(file);
            using var fingerprint = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            Add("pack-release-inputs-v1\n" + sdkVersion.Split('.')[0] + "\n" + policy.ToolchainRevision + "\n" + policy.Runtime + "\n" + JsonSerializer.Serialize(pack, Json));
            foreach (var file in inputs)
            {
                token.ThrowIfCancellationRequested(); string full = ProjectPath(root, file); RejectLinks(full);
                // Text checkout line endings must not turn an unchanged pack into a new release.
                byte[] content = await File.ReadAllBytesAsync(full, token);
                if (new[] { ".cs", ".csproj", ".resx", ".json", ".props", ".targets", ".xml", ".config", ".txt" }.Contains(Path.GetExtension(file)))
                    content = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(content).Replace("\r\n", "\n"));
                Add(file + "\0" + Convert.ToHexString(SHA256.HashData(content)) + "\n");
            }
            foreach (string package in packages) Add(package + "\n");
            string digest = Convert.ToHexString(fingerprint.GetHashAndReset()).ToLowerInvariant();
            var latest = previous.Where(m => m.Id == pack.Id).OrderByDescending(m => Version.Parse(m.Version)).FirstOrDefault();
            bool changed = latest?.ReleaseFingerprint != digest;
            string version = latest is null ? pack.InitialVersion : changed ? Increment(latest.Version) : latest.Version;
            planned.Add(new(pack.Id, pack.AssemblyName, pack.Project, version, pack.MinimumHostVersion, digest, changed, latest?.Version));

            void Add(string value) => fingerprint.AppendData(Encoding.UTF8.GetBytes(value));
            void Visit(string project)
            {
                string relative = Path.GetRelativePath(root, project).Replace('\\', '/');
                if (!visited.Add(relative)) return;
                if (visited.Count > 256 || !files.Contains(relative)) throw new InvalidDataException("Untracked or excessive pack project graph.");
                RejectLinks(project); inputs.Add(relative);
                string folder = Path.GetDirectoryName(project)!;
                string prefix = Path.GetRelativePath(root, folder).Replace('\\', '/') + "/";
                foreach (string file in files.Where(f => f.StartsWith(prefix, StringComparison.Ordinal) && !f.EndsWith(".md", StringComparison.OrdinalIgnoreCase))) inputs.Add(file);
                for (string? current = folder; current is not null && current.StartsWith(root, StringComparison.OrdinalIgnoreCase); current = Path.GetDirectoryName(current))
                    foreach (string name in new[] { "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "NuGet.Config", "nuget.config" })
                    {
                        string candidate = Path.GetRelativePath(root, Path.Combine(current, name)).Replace('\\', '/');
                        if (files.Contains(candidate)) inputs.Add(candidate);
                    }
                var document = XDocument.Load(project);
                foreach (var reference in document.Descendants().Where(e => e.Name.LocalName == "ProjectReference"))
                    Visit(ProjectPath(root, Path.GetRelativePath(root, Path.Combine(folder, Include(reference)))));
                foreach (var item in document.Descendants().Where(e => e.Name.LocalName is "Compile" or "EmbeddedResource" or "Content" or "None" or "AdditionalFiles" or "Analyzer" or "Import"))
                {
                    string? include = (string?)item.Attribute(item.Name.LocalName == "Import" ? "Project" : "Include");
                    if (include is null || include == "$(PowerTrovePackVersionsFile)") continue;
                    if (include.Contains('$') || include.Contains('@')) throw new InvalidDataException("Dynamic pack inputs need an explicit release-planning rule.");
                    string target = ProjectPath(root, Path.GetRelativePath(root, Path.Combine(folder, include.Replace('\\', '/').Split('*', '?')[0])));
                    string candidate = Path.GetRelativePath(root, target).Replace('\\', '/');
                    if (files.Contains(candidate)) inputs.Add(candidate);
                    else if (include.IndexOfAny(['*', '?']) >= 0)
                        // A partial filename glob (Shared/Info*.cs) has no directory separator
                        // after its literal prefix. Conservatively retain every matching prefix.
                        foreach (string file in files.Where(f => f.StartsWith(candidate.TrimEnd('/'), StringComparison.Ordinal))) inputs.Add(file);
                    else throw new InvalidDataException("Untracked explicit pack input.");
                }
                string assets = Path.Combine(folder, "obj", "project.assets.json");
                using var restored = JsonDocument.Parse(File.ReadAllText(assets));
                foreach (var library in restored.RootElement.GetProperty("libraries").EnumerateObject())
                    if (library.Value.GetProperty("type").GetString() == "package")
                        packages.Add(library.Name + "@" + library.Value.GetProperty("sha512").GetString());
            }
        }
        var plan = new PackReleasePlan(1, revision.StandardOutput.Trim(), sdkVersion, policy.Runtime,
            baseline is null ? null : Convert.ToHexString(SHA256.HashData(baseline)).ToLowerInvariant(), planned.ToArray());
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "pack-plan.json"), JsonSerializer.Serialize(plan, Json), token);
        var props = new XDocument(new XElement("Project", new XElement("ItemGroup", plan.Packs.Select(p =>
            new XElement("AssemblyMetadata", new XAttribute("Include", "PowerTrove.PackVersion"), new XAttribute("Value", p.Version),
                new XAttribute("Condition", "'$(MSBuildProjectName)' == '" + p.AssemblyName + "'"))))));
        await File.WriteAllTextAsync(Path.Combine(output, "pack-versions.props"), props.ToString(), token);
        return plan;
    }
    internal static string Increment(string value)
    {
        var v = Version.Parse(value);
        return new Version(v.Major, v.Minor, checked(Math.Max(v.Build, 0) + 1)).ToString();
    }
    private static bool SafeId(string value) => value.Length <= 128 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-');
    private static string Include(XElement element)
    {
        string include = (string?)element.Attribute("Include") ?? throw new InvalidDataException("Missing project reference.");
        if (include.IndexOfAny(['$', '@', '*', '?', ';']) >= 0) throw new InvalidDataException("Dynamic project references need explicit release planning.");
        return include.Replace('\\', '/');
    }
    private static string ProjectPath(string root, string relative)
    {
        string full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Pack inputs must remain within the source checkout.");
        return full;
    }
    private static void RejectLinks(string file)
    {
        for (string? current = file; current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Linked release inputs are not supported.");
    }
    private static async Task<byte[]> ReadBoundedAsync(string file, CancellationToken token)
    {
        if (new FileInfo(file).Length > ActionPackManifest.MaximumJsonBytes) throw new InvalidDataException("Catalog is too large.");
        return await File.ReadAllBytesAsync(file, token);
    }
    private static async Task<ActionManifest[]> ReadPreviousAsync(string root, string file, byte[] bytes, CancellationToken token)
    {
        byte[] signature = await File.ReadAllBytesAsync(file + ".sig", token);
        if (signature.Length is 0 or > 128) throw new InvalidDataException("Invalid catalog signature.");
        using var config = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "src/PowerTrove.Application/Resources/PackDistribution.json"), token));
        bool verified = false;
        foreach (var encoded in config.RootElement.GetProperty("TrustedKeys").EnumerateArray())
        {
            using var key = ECDsa.Create(); key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(encoded.GetString()!), out _);
            verified |= key.VerifyData(bytes, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        if (!verified) throw new InvalidDataException("Previous catalog signature is invalid.");
        using var json = JsonDocument.Parse(bytes);
        if (json.RootElement.GetProperty("schemaVersion").GetString() is not ("1" or "2")) throw new InvalidDataException("Unsupported catalog.");
        var manifests = json.RootElement.GetProperty("packs").EnumerateArray().Select(p => ActionPackManifest.Read(p.GetProperty("manifest").GetRawText())).ToArray();
        if (manifests.Length > 998 || manifests.Select(m => m.Id + "@" + m.Version).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifests.Length)
            throw new InvalidDataException("Invalid release catalog inventory.");
        return manifests;
    }
}
