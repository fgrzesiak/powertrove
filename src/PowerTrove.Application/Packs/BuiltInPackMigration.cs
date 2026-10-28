using PowerTrove.Application.Configuration;
using PowerTrove.Core.Catalog;

namespace PowerTrove.Application.Packs;

/// <summary>
/// Updating from a version with compiled-in packs to a Core-only version must not remove features: the owner
/// decided (ADR 0010) that those packs are downloaded automatically. Runs in the background, never blocks
/// startup, and keeps what is still missing for the next start when offline or interrupted.
/// </summary>
public sealed class BuiltInPackMigration(IPackManagementService packs, IPackMigrationSettings settings)
{
    /// <summary>Every pack the Full distribution compiled in before Core-only releases.</summary>
    public static readonly IReadOnlyList<string> FormerlyBuiltIn =
        ["powertrove.files", "powertrove.images", "powertrove.pdf", "powertrove.media", "powertrove.data",
         "powertrove.office", "powertrove.tables", "powertrove.einvoice"];

    /// <summary>
    /// Decides once whether this installation takes over packs and adds the packs chosen in Setup.
    /// Returns whether any remain to install.
    /// </summary>
    public bool Prepare(IReadOnlyCollection<string> compiledIn, IReadOnlyCollection<string>? setupSelection = null)
    {
        if (!settings.PackMigrationChecked)
            // A fresh installation chooses its packs in Setup; only an existing installation is migrated.
            settings.SetPackMigration(settings.HadExistingSettings ? FormerlyBuiltIn.Where(id => !compiledIn.Contains(id)).ToArray() : []);
        if (setupSelection is { Count: > 0 })
            settings.SetPackMigration(settings.PendingPackMigration.Concat(setupSelection.Where(id => !compiledIn.Contains(id))).ToArray());
        return settings.PendingPackMigration.Count > 0;
    }

    /// <summary>
    /// Reads the selection Setup wrote next to the app. Only well-formed official pack IDs are accepted;
    /// anything else is ignored, and the packs are still verified like any explicit installation.
    /// </summary>
    public static IReadOnlyList<string> ReadSetupSelection(string file)
    {
        var info = new FileInfo(file);
        if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length > 4096) return [];
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(file));
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("packs", out var packs) || packs.ValueKind != System.Text.Json.JsonValueKind.Array) return [];
            return packs.EnumerateArray().Where(p => p.ValueKind == System.Text.Json.JsonValueKind.String).Select(p => p.GetString()!)
                .Where(id => FormerlyBuiltIn.Contains(id, StringComparer.Ordinal)).Distinct(StringComparer.Ordinal).ToArray();
        }
        catch (System.Text.Json.JsonException) { return []; }
    }

    /// <summary>Records what is still to install for the next start, or removes the selection when nothing remains.</summary>
    public static void WriteSetupSelection(string file, IReadOnlyCollection<string> remaining)
    {
        if (remaining.Count == 0) { File.Delete(file); return; }
        File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(new { packs = remaining }));
    }

    /// <summary>Reads and removes the selection; the app then installs it like takeover packs.</summary>
    public static IReadOnlyList<string> TakeSetupSelection(string file)
    {
        try
        {
            if ((new FileInfo(file) is { Exists: true } info) && (info.Attributes & FileAttributes.ReparsePoint) != 0) return [];
            var ids = ReadSetupSelection(file);
            if (File.Exists(file)) File.Delete(file);
            return ids;
        }
        // An undeletable selection would be applied again; a later start retries instead.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    public async Task RunAsync(CancellationToken token)
    {
        var pending = settings.PendingPackMigration;
        if (pending.Count == 0) return;
        var page = await packs.OpenOnUserRequestAsync(token).ConfigureAwait(false);
        if (page.Status != PackChannelStatus.Ready) return;
        var remaining = new List<string>();
        foreach (string id in pending)
        {
            token.ThrowIfCancellationRequested();
            var row = page.Packs.FirstOrDefault(p => p.Id == id);
            // Already installed (for example from Settings), or not offered by this channel: nothing to take over.
            if (row is null || row.State is PackInstallState.Installed or PackInstallState.UpdateAvailable) continue;
            if (!row.CanDownload) { remaining.Add(id); continue; }
            var offer = await packs.PrepareAsync(id, token).ConfigureAwait(false);
            var status = offer.Offer is { } prepared ? await packs.InstallAsync(prepared, null, token).ConfigureAwait(false) : offer.Status;
            if (status != PackChannelStatus.Installed) remaining.Add(id);
        }
        settings.SetPackMigration(remaining);
    }
}
