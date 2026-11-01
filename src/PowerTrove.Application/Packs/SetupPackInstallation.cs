namespace PowerTrove.Application.Packs;

/// <summary>
/// Setup runs this before finishing an installation, so selected missing packs are downloaded and verified while
/// Setup is still open, and selected installed packs receive a newer compatible version. It uses the same signed
/// channel and package store as Settings. A missing pack that cannot be installed now, for example while offline,
/// stays selected and the app installs it at its next start; an installed pack simply keeps its current version.
/// </summary>
public sealed class SetupPackInstallation(PackChannelClient channel, IReadOnlyDictionary<string, string>? installedVersions = null)
{
    /// <summary>Installs or updates the selected packs and returns those still missing.</summary>
    public async Task<IReadOnlyList<string>> InstallAsync(IReadOnlyList<string> selection, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var selected = selection.Distinct(StringComparer.Ordinal).ToArray();
        if (selected.Length == 0) return [];
        var missing = selected.Where(id => installedVersions?.ContainsKey(id) != true).ToArray();
        if ((await channel.RefreshOnUserRequestAsync(token).ConfigureAwait(false)).Status != PackChannelStatus.Ready) return missing;
        var remaining = new List<string>();
        foreach (string id in selected)
        {
            token.ThrowIfCancellationRequested();
            string? current = installedVersions?.GetValueOrDefault(id);
            var offer = await channel.PrepareOnUserRequestAsync(id, token).ConfigureAwait(false);
            if (current is not null && (offer.Offer is not { } update || !IsNewer(update.Version, current))) continue;
            var status = offer.Offer is { } prepared
                ? (await channel.InstallOnUserRequestAsync(id, null, token, prepared).ConfigureAwait(false)).Status : offer.Status;
            if (status != PackChannelStatus.Installed && current is null) remaining.Add(id);
        }
        return remaining;
    }

    private static bool IsNewer(string candidate, string current) =>
        Version.TryParse(candidate, out var offered) && Version.TryParse(current, out var installed) && offered > installed;
}
