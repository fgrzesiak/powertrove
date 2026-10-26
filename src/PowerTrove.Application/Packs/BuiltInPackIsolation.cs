using System.Security.Cryptography;
using PowerTrove.Abstractions;
using PowerTrove.Abstractions.Files;
using PowerTrove.Abstractions.Manifest;

namespace PowerTrove.Application.Packs;

/// <summary>Routes app-owned heavy tools through pinned, supervised workers when explicitly enabled.</summary>
public static class BuiltInPackIsolation
{
    private static readonly string[] Heavy = ["powertrove.pdf", "powertrove.images", "powertrove.media"];
    public sealed record IsolationResult(IReadOnlyList<IActionPack> Packs, bool Ready);

    public static async Task<IsolationResult> RouteAsync(
        IReadOnlyList<IActionPack> packs, PackHostSupervisor supervisor, string appDirectory,
        IReadOnlySet<string>? imagePresetIds = null, CancellationToken token = default)
    {
        var isolated = packs.Where(p => Heavy.Contains(p.Info.Id, StringComparer.Ordinal)).ToArray();
        if (isolated.Length == 0) return new(packs, true);
        try
        {
            var root = Path.GetFullPath(appDirectory);
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException();
            string manifests = Path.Combine(root, "pack-manifests");
            if (Directory.Exists(manifests) && (File.GetAttributes(manifests) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException();
            var inventory = await InventoryAsync(root, token);
            var prepared = new List<(string Path, byte[] Content, string Hash)>();
            foreach (var pack in isolated)
            {
                string entry = "PowerTrove.Pack." + (pack.Info.Id switch
                {
                    "powertrove.pdf" => "Pdf", "powertrove.images" => "Images",
                    "powertrove.media" => "Media", _ => throw new InvalidDataException()
                }) + ".Composition.dll";
                if (!inventory.Files.Any(f => f.Path == entry)) throw new InvalidDataException();
                // User-created image presets require per-user options unavailable to worker v1.
                var actions = pack.Actions.Where(a => pack.Info.Id != "powertrove.images" ||
                    imagePresetIds?.Contains(a.Descriptor.Id) != true).ToArray();
                var manifest = ActionPackManifest.Create(new SimplePack(pack.Info, actions),
                    minHostVersion: new Version(0, 1, 0), payload: inventory with { EntryAssembly = entry });
                if (manifest.Actions.Any(a => a.InputKinds.Count != 1 || a.InputKinds[0] != "files"))
                    throw new InvalidDataException();
                byte[] content = System.Text.Encoding.UTF8.GetBytes(ActionPackManifest.ToJson(manifest));
                prepared.Add((Path.Combine(manifests, pack.Info.Id + ".json"), content,
                    Convert.ToHexString(SHA256.HashData(content))));
            }
            Directory.CreateDirectory(manifests);
            foreach (string id in Heavy)
            {
                string path = Path.Combine(manifests, id + ".json");
                if (File.Exists(path)) File.Delete(path);
            }
            foreach (var item in prepared)
            {
                string temporary = item.Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await File.WriteAllBytesAsync(temporary, item.Content, token);
                    File.Move(temporary, item.Path);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            var group = new PackWorkerGroup("builtin-file-tools", [root],
                isolated.Select(p => new PackWorkerIdentity(p.Info.Id, p.Info.Version)).ToArray(),
                prepared.Select(p => p.Hash).ToArray());
            return new(packs.Select(p => Heavy.Contains(p.Info.Id, StringComparer.Ordinal)
                ? (IActionPack)new SimplePack(p.Info, p.Actions.Select(a =>
                    p.Info.Id == "powertrove.images" && imagePresetIds?.Contains(a.Descriptor.Id) == true ? a
                        : (IAction)new RemoteAction(supervisor, group, a.Descriptor,
                            (a as IFileActionMetadata)?.InputExtensions)).ToArray())
                : p).ToArray(), true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
            System.Text.Json.JsonException or CryptographicException)
        {
            // A failed requested isolation must never silently run heavy tools in-process.
            return new(packs.Select(p => Heavy.Contains(p.Info.Id, StringComparer.Ordinal)
                ? (IActionPack)new SimplePack(p.Info, p.Actions.Select(a => (IAction)new Unavailable(a.Descriptor)).ToArray())
                : p).ToArray(), false);
        }
    }

    private static async Task<ManifestPayload> InventoryAsync(string root, CancellationToken token)
    {
        long total = 0;
        var files = new List<ManifestPayloadFile>();
        foreach (var path in Directory.EnumerateFiles(root, "*", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        })
                     .OrderBy(x => x, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (relative.StartsWith("pack-manifests/", StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(path).Equals("PowerTrove.Abstractions.dll", StringComparison.OrdinalIgnoreCase) ||
                relative.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) ||
                relative.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ||
                relative.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException();
            long length = new FileInfo(path).Length;
            total = checked(total + length);
            if (total > 1024L * 1024 * 1024 || files.Count >= 10000) throw new InvalidDataException();
            await using var stream = File.OpenRead(path);
            files.Add(new(relative, Convert.ToHexString(await SHA256.HashDataAsync(stream, token)), length));
        }
        if (files.Count == 0) throw new InvalidDataException();
        return new ManifestPayload(files[0].Path, total, files);
    }

    private sealed record SimplePack(ActionPackInfo Info, IReadOnlyList<IAction> Actions) : IActionPack;
    private sealed class Unavailable(ActionDescriptor descriptor) : IAction
    {
        public ActionDescriptor Descriptor => descriptor;
        public ValueTask<ActionAvailability> MatchAsync(ContextSnapshot context, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return new(new ActionAvailability(false, PackStrings.Stopped)); }
        public ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters parameters, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return new(new ActionPreview(false, PackStrings.Stopped, [], [], IsBlocked: true)); }
        public Task<ActionReceipt> ExecuteAsync(ContextSnapshot context, ActionParameters parameters,
            IProgress<ActionProgress>? progress, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Task.FromResult(new ActionReceipt(false, false, [], [], false, Message: PackStrings.Stopped)); }
    }
}
