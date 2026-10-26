using System.Diagnostics;
using System.Globalization;
using ContextBar.Abstractions.Office;
using ContextBar.Abstractions.Ocr;
using ContextBar.Abstractions.Packs;
using ContextBar.Infrastructure.Files;
using ContextBar.Platform.Windows.Imaging;

namespace ContextBar.PackHost;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsWindows()) return 2;
        // Protocol uses raw stdout; pack/library Console output and stderr never become content diagnostics.
        using var input = Console.OpenStandardInput();
        using var output = Console.OpenStandardOutput();
        Console.SetOut(TextWriter.Null);
        Console.SetError(TextWriter.Null);
        var loaded = new List<LoadedPack>();
        using var lifetime = new CancellationTokenSource();
        Process? parent = null;
        Task? parentWatch = null;
        string? ownedWork = null;
        try
        {
            var options = ParseArguments(args);
            if (options is null) return 2;
            CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(options.Language);
            parent = Process.GetProcessById(options.ParentPid);
            using var current = Process.GetCurrentProcess();
            if (parent.HasExited || parent.Id == current.Id || parent.SessionId != current.SessionId) return 2;
            parentWatch = WatchParentAsync(parent, lifetime);
            string userRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ContextBar");
            string workRoot = Path.Combine(userRoot, "workers");
            Directory.CreateDirectory(workRoot);
            string candidateWork = options.WorkRoot is null ? Path.Combine(workRoot, Guid.NewGuid().ToString("N"))
                : StagingOutputFileSystem.Canonical(options.WorkRoot);
            if (!Guid.TryParseExact(Path.GetRelativePath(workRoot, candidateWork), "N", out _)) return 2;
            StagingOutputFileSystem.RejectReparseAncestors(candidateWork);
            if (Directory.Exists(candidateWork) && Directory.EnumerateFileSystemEntries(candidateWork).Any()) return 2;
            ownedWork = candidateWork;
            Directory.CreateDirectory(ownedWork);
            var version = typeof(Program).Assembly.GetName().Version ?? new Version(0, 1, 0);
            foreach (string directory in options.PackDirectories)
            {
                string root = StagingOutputFileSystem.Canonical(directory);
                StagingOutputFileSystem.RejectReparseAncestors(root);
                string direct = Path.Combine(root, "manifest.json");
                string builtIn = Path.Combine(root, "pack-manifests");
                var manifests = File.Exists(direct) ? new[] { direct }
                    : Directory.Exists(builtIn) ? Directory.GetFiles(builtIn, "*.json", SearchOption.TopDirectoryOnly) : Array.Empty<string>();
                if (manifests.Length is 0 or > 64 || loaded.Count + manifests.Length > 64) return 2;
                foreach (string manifest in manifests.OrderBy(p => p, StringComparer.Ordinal))
                    loaded.Add(await LoadedPack.LoadAsync(root, manifest, ownedWork, version, lifetime.Token,
                        options.ManifestHashes.Count == 0 ? null : options.ManifestHashes.ToHashSet(StringComparer.OrdinalIgnoreCase)).ConfigureAwait(false));
            }
            if (loaded.Select(p => p.Manifest.Id).Distinct(StringComparer.Ordinal).Count() != loaded.Count) return 2;
            if (options.ManifestHashes.Count != 0 && options.ManifestHashes.Count != loaded.Count) return 2;
            string staging = Path.Combine(userRoot, "staging");
            Directory.CreateDirectory(staging);
            var verifier = new FileSignatureVerifier();
            IPackHostServices Services(FileActionServices files) => new PackHostServices(version, files)
                .Add<ITextRecognizer>(new WindowsTextRecognizer()).Add<IOfficeInputValidator>(new OfficeInputValidator());
            var declaredIds = loaded.SelectMany(p => p.Manifest.Actions).Select(a => a.Id).ToArray();
            if (declaredIds.Distinct(StringComparer.Ordinal).Count() != declaredIds.Length) return 2;
            var session = new WorkerSession(loaded.Select(p => p.Factory).ToArray(), Services, verifier, staging,
                declaredActionIds: declaredIds.ToHashSet(StringComparer.Ordinal));
            await session.RunAsync(input, output, lifetime.Token).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return 0; }
        catch (Exception) { return 3; }
        finally
        {
            lifetime.Cancel();
            if (parentWatch is not null) { try { await parentWatch.ConfigureAwait(false); } catch (OperationCanceledException) { } }
            parent?.Dispose();
            foreach (var pack in loaded) pack.Dispose();
            // The owning supervisor performs final cleanup after exit, when native payload locks are released.
            if (ownedWork is not null) { try { Directory.Delete(ownedWork, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }
    }

    public sealed record Options(IReadOnlyList<string> PackDirectories, int ParentPid, string Language, string? WorkRoot, IReadOnlyList<string> ManifestHashes);
    public static Options? ParseArguments(IReadOnlyList<string> args)
    {
        if (args.Count > 264 || args.Count % 2 != 0) return null;
        var directories = new List<string>(); int? pid = null; string? language = null, work = null;
        var hashes = new List<string>();
        for (int i = 0; i < args.Count; i += 2)
        {
            string value = args[i + 1];
            if (value.Length is 0 or > 4096) return null;
            switch (args[i])
            {
                case "--pack-dir": directories.Add(value); break;
                case "--manifest-sha256" when value.Length == 64 && value.All(Uri.IsHexDigit): hashes.Add(value); break;
                case "--parent-pid" when pid is null && int.TryParse(value, out int parsed) && parsed > 0: pid = parsed; break;
                case "--ui-language" when language is null && value is "en" or "de": language = value; break;
                case "--work-root" when work is null: work = value; break;
                default: return null;
            }
        }
        return pid is not null && directories.Count is > 0 and <= 64 && hashes.Count <= 64
            && hashes.Distinct(StringComparer.OrdinalIgnoreCase).Count() == hashes.Count
            ? new Options(directories.AsReadOnly(), pid.Value, language ?? "en", work, hashes.AsReadOnly()) : null;
    }
    private static async Task WatchParentAsync(Process parent, CancellationTokenSource lifetime)
    {
        try { await parent.WaitForExitAsync(lifetime.Token).ConfigureAwait(false); lifetime.Cancel(); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }
}
