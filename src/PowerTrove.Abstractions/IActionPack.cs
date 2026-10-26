using System.Collections.Generic;

namespace PowerTrove.Abstractions;

/// <summary>
/// Identity of an action pack. Matches the top-level fields of the action pack
/// manifest (<c>docs/references/action-pack-manifest.md</c>).
/// </summary>
public sealed record ActionPackInfo(
    string Id,
    string DisplayName,
    string Version,
    string Publisher);

/// <summary>
/// A cohesive, separately versioned group of actions (for example PDF or image
/// tools). Built-in packs are compiled in and registered by the host's
/// composition root; nothing is loaded dynamically. A pack depends only on
/// PowerTrove.Abstractions and its own engines, so it can later move to its own
/// repository or become an out-of-process extension without code changes.
/// </summary>
public interface IActionPack
{
    ActionPackInfo Info { get; }

    IReadOnlyList<IAction> Actions { get; }
}
