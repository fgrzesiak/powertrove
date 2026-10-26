using ContextBar.Abstractions.Manifest;

namespace ContextBar.Core.Catalog;

public enum PackInstallState { Installed, NotInstalled, UpdateAvailable, Incompatible }

public sealed record CatalogEntry(ManifestAction Action, ActionPackInfo Pack, PackInstallState State, long DownloadBytes);

public interface IActionCatalog
{
    IReadOnlyList<CatalogEntry> Entries { get; }
}

/// <summary>A bounded, local discovery document was rejected. Never includes its contents.</summary>
public sealed class CatalogFormatException : Exception
{
    public CatalogFormatException() : base("Invalid action catalog.") { }
}
