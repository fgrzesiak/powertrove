using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using PowerTrove.Abstractions.Manifest;
using PowerTrove.Abstractions.Packs;

namespace PowerTrove.PackHost;

/// <summary>Loads first-party installed manifests into independent contexts after verifying a private payload copy.</summary>
public sealed class LoadedPack : IDisposable
{
    private readonly PackLoadContext _context;
    private readonly string _shadow;
    public ActionManifest Manifest { get; }
    public IPackFactory Factory { get; }
    private LoadedPack(ActionManifest manifest, IPackFactory factory, PackLoadContext context, string shadow)
    { Manifest = manifest; Factory = factory; _context = context; _shadow = shadow; }

    public static async Task<LoadedPack> LoadAsync(string directory, string manifestPath, string workRoot,
        Version hostVersion, CancellationToken cancellationToken = default, IReadOnlySet<string>? trustedManifestHashes = null)
    {
        directory = StagingOutputFileSystem.Canonical(directory);
        StagingOutputFileSystem.RejectReparseAncestors(directory);
        manifestPath = StagingOutputFileSystem.Canonical(manifestPath);
        if (!manifestPath.StartsWith(directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Manifest is outside the pack directory.");
        StagingOutputFileSystem.RejectReparseAncestors(manifestPath);
        ActionManifest manifest;
        await using (var stream = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
        {
            if (stream.Length > ActionPackManifest.MaximumJsonBytes) throw new InvalidDataException("Manifest is too large.");
            byte[] bytes = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            if (await stream.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false) != 0)
                throw new InvalidDataException("Manifest changed during verification.");
            if (trustedManifestHashes is not null && !trustedManifestHashes.Contains(Convert.ToHexString(SHA256.HashData(bytes))))
                throw new InvalidDataException("Installed manifest changed after authorization.");
            manifest = ActionPackManifest.Read(new UTF8Encoding(false, true).GetString(bytes));
        }
        if (manifest.SchemaVersion != "2.0" || manifest.ProtocolVersion != "1" || manifest.Payload is not { } payload || payload.SizeBytes > 1024L * 1024 * 1024
            || !Version.TryParse(manifest.MinHostVersion, out var minimum) || minimum > hostVersion)
            throw new InvalidDataException("Pack payload or host version is unsupported.");
        workRoot = StagingOutputFileSystem.Canonical(workRoot);
        StagingOutputFileSystem.RejectReparseAncestors(workRoot);
        string shadow = Path.Combine(workRoot, "loads", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(shadow);
        PackLoadContext? context = null;
        try
        {
            long total = 0;
            foreach (var file in payload.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string source = Path.Combine(directory, file.Path.Replace('/', Path.DirectorySeparatorChar));
                StagingOutputFileSystem.RejectReparseAncestors(source);
                await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                long length = input.Length, copied = 0;
                total = checked(total + length);
                if (total > payload.SizeBytes || file.SizeBytes is { } expected && length != expected)
                    throw new InvalidDataException("Pack payload length mismatch.");
                string destination = Path.Combine(shadow, file.Path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                byte[] buffer = new byte[81920]; int count;
                while ((count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    copied = checked(copied + count);
                    if (copied > length) throw new InvalidDataException("Pack payload changed during verification.");
                    hash.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                }
                if (copied != length || !Convert.ToHexString(hash.GetHashAndReset()).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Pack payload hash mismatch.");
            }
            if (total != payload.SizeBytes) throw new InvalidDataException("Pack payload inventory mismatch.");
            string entry = Path.Combine(shadow, payload.EntryAssembly.Replace('/', Path.DirectorySeparatorChar));
            context = new PackLoadContext(entry, shadow, payload.Files.Select(f => f.Path));
            var assembly = context.LoadFromAssemblyPath(entry);
            var attributes = assembly.GetCustomAttributes<ActionPackFactoryAttribute>().ToArray();
            if (attributes.Length != 1 || attributes[0].FactoryType.Assembly != assembly
                || !attributes[0].FactoryType.IsPublic || attributes[0].FactoryType.IsAbstract
                || !typeof(IPackFactory).IsAssignableFrom(attributes[0].FactoryType)
                || attributes[0].FactoryType.GetConstructor(Type.EmptyTypes) is null
                || Activator.CreateInstance(attributes[0].FactoryType) is not IPackFactory factory
                || factory.Info.Id != manifest.Id || factory.Info.Version != manifest.Version)
                throw new InvalidDataException("Pack factory does not match its manifest.");
            return new LoadedPack(manifest, factory, context, shadow);
        }
        catch
        {
            context?.Unload();
            TryDelete(shadow);
            throw;
        }
    }

    public void Dispose() { _context.Unload(); TryDelete(_shadow); }
    private static void TryDelete(string directory)
    {
        try { Directory.Delete(directory, recursive: true); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
        // The supervisor removes its owned work root after the process exits and native DLL locks are released.
    }
}

internal sealed class PackLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    private readonly string _root;
    private readonly string _entryDirectory;
    private readonly HashSet<string> _files;
    private readonly string _contractName = typeof(IPackFactory).Assembly.GetName().Name!;
    public PackLoadContext(string entry, string root, IEnumerable<string> files) : base(isCollectible: true)
    { _resolver = new(entry); _root = root; _entryDirectory = Path.GetDirectoryName(entry)!; _files = new(files, StringComparer.OrdinalIgnoreCase); }

    protected override Assembly? Load(AssemblyName name)
    {
        if (name.Name == _contractName) return typeof(IPackFactory).Assembly;
        if (name.Name is "mscorlib" or "netstandard" or "System" or "Microsoft.CSharp" or "WinRT.Runtime" or "Microsoft.Windows.SDK.NET"
            || name.Name?.StartsWith("System.", StringComparison.Ordinal) == true || name.Name?.StartsWith("Microsoft.Win32.", StringComparison.Ordinal) == true)
            return Default.LoadFromAssemblyName(name);
        string? path = _resolver.ResolveAssemblyToPath(name);
        if (path is null && name.Name is not null)
        {
            string candidate = string.IsNullOrEmpty(name.CultureName) ? Path.Combine(_entryDirectory, name.Name + ".dll")
                : Path.Combine(_entryDirectory, name.CultureName, name.Name + ".dll");
            if (File.Exists(candidate)) path = candidate;
        }
        if (path is null || !Known(path)) throw new FileNotFoundException("Pack dependency is not in the verified payload.");
        return LoadFromAssemblyPath(path);
    }
    protected override IntPtr LoadUnmanagedDll(string name)
    {
        string? path = _resolver.ResolveUnmanagedDllToPath(name);
        if (path is not null && Known(path)) return LoadUnmanagedDllFromPath(path);
        string simple = Path.GetFileNameWithoutExtension(name).ToLowerInvariant();
        if (name.IndexOfAny(['/', '\\', ':']) < 0 && (simple is "kernel32" or "user32" or "advapi32" or "shell32" or "ole32" or "oleaut32" or "combase" or "windowscodecs" or "propsys"
            || simple.StartsWith("api-ms-win-", StringComparison.Ordinal))) return IntPtr.Zero;
        throw new DllNotFoundException("Native dependency is not in the verified payload.");
    }
    private bool Known(string path)
    {
        string relative = Path.GetRelativePath(_root, path).Replace(Path.DirectorySeparatorChar, '/');
        return !relative.StartsWith("../", StringComparison.Ordinal) && _files.Contains(relative);
    }
}
