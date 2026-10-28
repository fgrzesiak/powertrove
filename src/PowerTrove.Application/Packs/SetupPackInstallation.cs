namespace PowerTrove.Application.Packs;

/// <summary>
/// Setup runs this before finishing a new installation, so the selected packs are downloaded and verified while
/// Setup is still open. It uses the same signed channel and package store as Settings. What cannot be installed now,
/// for example while offline, stays selected and the app installs it at its next start.
/// </summary>
public sealed class SetupPackInstallation(PackChannelClient channel)
{
    /// <summary>Installs the selected packs and returns those still missing.</summary>
    public async Task<IReadOnlyList<string>> InstallAsync(IReadOnlyList<string> selection, CancellationToken token)
    {
        if (selection.Count == 0) return [];
        if ((await channel.RefreshOnUserRequestAsync(token).ConfigureAwait(false)).Status != PackChannelStatus.Ready) return selection;
        var remaining = new List<string>();
        foreach (string id in selection)
        {
            token.ThrowIfCancellationRequested();
            var offer = await channel.PrepareOnUserRequestAsync(id, token).ConfigureAwait(false);
            var status = offer.Offer is { } prepared
                ? (await channel.InstallOnUserRequestAsync(id, null, token, prepared).ConfigureAwait(false)).Status : offer.Status;
            if (status != PackChannelStatus.Installed) remaining.Add(id);
        }
        return remaining;
    }
}
