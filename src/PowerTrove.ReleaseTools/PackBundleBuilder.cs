using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PowerTrove.Abstractions;
using PowerTrove.Abstractions.Files;
using PowerTrove.Abstractions.Manifest;
using PowerTrove.Abstractions.Packs;

namespace PowerTrove.ReleaseTools;

/// <summary>
/// Local, explicit first-party bundle producer. Never generates or exports a private key.
/// Unsigned dry runs produce metadata ONLY, never installable .cbpack files.
/// </summary>
public static class PackBundleBuilder
{
    private const long MaximumPayloadBytes = 1024L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private static readonly string[] SharedAssemblies =
    [
        "PowerTrove.Abstractions.dll",
        "Microsoft.Windows.SDK.NET.dll",
        "WinRT.Runtime.dll"
    ];

    public static async Task BuildAsync(string publishedRoot, string outputDirectory, string? keyFile,
        bool dryRun, CancellationToken cancellationToken = default)
    {
        publishedRoot = Path.GetFullPath(publishedRoot);
        outputDirectory = Path.GetFullPath(outputDirectory);
        if (!Directory.Exists(publishedRoot)) throw new DirectoryNotFoundException("Published pack inputs are missing.");
        var directories = Directory.GetDirectories(publishedRoot).OrderBy(p => p, StringComparer.Ordinal).ToArray();
        if (directories.Length is < 1 or > 64) throw new InvalidDataException("Expected between 1 and 64 published packs.");
        string final = Path.Combine(outputDirectory, "packs");
        if (Directory.Exists(final) || File.Exists(final)) throw new IOException("Pack output already exists; use a fresh output directory.");

        using ECDsa? signer = dryRun ? null : LoadSigner(keyFile);
        Directory.CreateDirectory(outputDirectory);
        string staging = Path.Combine(outputDirectory, ".packs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var published = new List<(ActionManifest Manifest, long DownloadBytes)>();
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var actionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string directory in directories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string name = Path.GetFileName(directory);
                string entry = name + ".Composition.dll";
                string entryPath = Path.Combine(directory, entry);
                if (!File.Exists(entryPath)) throw new InvalidDataException("Composition entry assembly is missing.");
                ManifestPayload payload = Inventory(directory, entry);
                using var context = new MetadataContext(entryPath);
                var assembly = context.LoadFromAssemblyPath(entryPath);
                var attributes = assembly.GetCustomAttributes<ActionPackFactoryAttribute>().ToArray();
                if (attributes.Length != 1 || attributes[0].FactoryType.Assembly != assembly
                    || !typeof(IPackFactory).IsAssignableFrom(attributes[0].FactoryType)
                    || attributes[0].FactoryType.GetConstructor(Type.EmptyTypes) is null
                    || Activator.CreateInstance(attributes[0].FactoryType) is not IPackFactory factory)
                    throw new InvalidDataException("Published composition has no valid first-party factory.");
                var services = new PackHostServices(new Version(0, 1, 0),
                    FileActionServices.CreatePhysical(new MetadataOnlyVerifier()));
                var pack = factory.Create(services);
                if (pack.Info != factory.Info) throw new InvalidDataException("Factory metadata is inconsistent.");
                ActionManifest manifest = ActionPackManifest.Create(pack, minHostVersion: new Version(0, 1, 0), payload: payload);
                _ = ActionPackManifest.ToJson(manifest);
                if (!ids.Add(manifest.Id) || manifest.Actions.Any(a => !actionIds.Add(a.Id)))
                    throw new InvalidDataException("Duplicate pack or action identifier.");
                // Worker protocol v1 carries file selections only. Signing a non-file pack
                // would make an apparently installable bundle that the installer refuses.
                if (manifest.Actions.Any(a => a.InputKinds.Count != 1 || a.InputKinds[0] != "files"))
                    throw new InvalidDataException("A composition uses selection kinds unsupported by worker protocol v1.");

                byte[] manifestBytes = Encoding.UTF8.GetBytes(ActionPackManifest.ToJson(manifest));
                long downloadBytes = 0;
                if (dryRun)
                {
                    string folder = Path.Combine(staging, "metadata", manifest.Id);
                    Directory.CreateDirectory(folder);
                    await File.WriteAllBytesAsync(Path.Combine(folder, "manifest.json"), manifestBytes, cancellationToken);
                }
                else
                {
                    string folder = Path.Combine(staging, manifest.Id);
                    Directory.CreateDirectory(folder);
                    string bundle = Path.Combine(folder, manifest.Version + ".cbpack");
                    WriteSignedBundle(bundle, directory, manifest, manifestBytes, signer!);
                    downloadBytes = new FileInfo(bundle).Length;
                    if (downloadBytes > MaximumPayloadBytes + ActionPackManifest.MaximumJsonBytes + 1024 * 1024)
                        throw new InvalidDataException("Bundle exceeds installer limit.");
                }
                published.Add((manifest, downloadBytes));
            }
            byte[] catalogBytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = "1",
                packs = published.Select(p => new { manifest = p.Manifest, downloadBytes = p.DownloadBytes }).ToArray()
            }, JsonOptions);
            if (catalogBytes.Length > 4 * 1024 * 1024) throw new InvalidDataException("Catalog exceeds reader limit.");
            await File.WriteAllBytesAsync(Path.Combine(staging, "catalog.json"), catalogBytes, cancellationToken);
            if (signer is not null)
                await File.WriteAllBytesAsync(Path.Combine(staging, "catalog.json.sig"),
                    signer.SignData(catalogBytes, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(staging, final);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    private static ECDsa LoadSigner(string? path)
    {
        path = string.IsNullOrWhiteSpace(path) ? Environment.GetEnvironmentVariable("POWERTROVE_PACK_SIGNING_KEY_FILE") : path;
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("A private signing key file is required outside dry-run mode.");
        var signer = ECDsa.Create();
        try
        {
            signer.ImportFromPem(File.ReadAllText(path));
            if (signer.KeySize != 256 || signer.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7"
                || signer.ExportParameters(true).D is null)
                throw new CryptographicException("Signing key must be a P-256 private key.");
            return signer;
        }
        catch { signer.Dispose(); throw; }
    }

    internal static ManifestPayload Inventory(string root, string entryAssembly)
    {
        var files = new List<ManifestPayloadFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (relative.Split('/').Any(s => s is "." or ".." || s.Length == 0))
                throw new InvalidDataException("Unsafe payload path.");
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Reparse point is not allowed in payload.");
            if (SharedAssemblies.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                || relative.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)
                || relative.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                continue;
            string packPath = "payload/" + relative;
            if (!seen.Add(packPath)) throw new InvalidDataException("Duplicate payload file path.");
            long size = new FileInfo(path).Length;
            total = checked(total + size);
            if (total > MaximumPayloadBytes || files.Count == 10000)
                throw new InvalidDataException("Pack exceeds inventory limits.");
            // File hashes always describe the exact bytes supplied to the installer.
            files.Add(new ManifestPayloadFile(packPath,
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), size));
        }
        string entry = "payload/" + entryAssembly;
        if (!seen.Contains(entry) || files.Count == 0) throw new InvalidDataException("Entry assembly is not in the payload.");
        return new ManifestPayload(entry, total, files);
    }

    internal static void WriteSignedBundle(string destination, string publishedDirectory,
        ActionManifest manifest, byte[] manifestBytes, ECDsa signer)
    {
        var payload = manifest.Payload ?? throw new InvalidDataException("Missing payload.");
        byte[] signature = signer.SignData(manifestBytes, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        using var zip = ZipFile.Open(destination, ZipArchiveMode.Create);
        WriteBytes(zip, "manifest.json", manifestBytes);
        WriteBytes(zip, "manifest.sig", signature);
        foreach (var file in payload.Files)
        {
            if (!file.Path.StartsWith("payload/", StringComparison.Ordinal)) throw new InvalidDataException("Unsafe inventory.");
            string source = Path.Combine(publishedDirectory, file.Path["payload/".Length..].Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(source) || (File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Payload changed after hashing.");
            byte[] content = File.ReadAllBytes(source);
            if (content.LongLength != file.SizeBytes ||
                !Convert.ToHexString(SHA256.HashData(content)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Payload changed after hashing.");
            WriteBytes(zip, file.Path, content);
        }
    }

    private static void WriteBytes(ZipArchive zip, string name, byte[] content)
    {
        using var stream = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        stream.Write(content);
    }

    private sealed class MetadataOnlyVerifier : IFileTypeVerifier
    {
        public ValueTask<FileTypeVerification> VerifyAsync(string path, FileInputFilter filter, CancellationToken token) =>
            throw new NotSupportedException("Packaging never inspects user files.");
    }

    private sealed class MetadataContext : AssemblyLoadContext, IDisposable
    {
        private readonly AssemblyDependencyResolver _resolver;
        public MetadataContext(string entry) : base(isCollectible: true) => _resolver = new(entry);
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name == typeof(IPackFactory).Assembly.GetName().Name) return typeof(IPackFactory).Assembly;
            string? resolved = _resolver.ResolveAssemblyToPath(name);
            return resolved is null ? null : LoadFromAssemblyPath(resolved);
        }
        public void Dispose() => Unload();
    }
}
