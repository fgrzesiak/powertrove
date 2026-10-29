using PowerTrove.Abstractions.Manifest;

namespace PowerTrove.Core.Catalog;

public enum PackInstallState { Installed, NotInstalled, UpdateAvailable, Incompatible }

public sealed record CatalogEntry(ManifestAction Action, ActionPackInfo Pack, PackInstallState State, long DownloadBytes, string? ManifestFingerprint = null)
{
    public string? EnglishTitle { get; init; }
}

public interface IActionCatalog
{
    IReadOnlyList<CatalogEntry> Entries { get; }
}

/// <summary>A bounded, local discovery document was rejected. Never includes its contents.</summary>
public sealed class CatalogFormatException : Exception
{
    public CatalogFormatException() : base("Invalid action catalog.") { }
}
