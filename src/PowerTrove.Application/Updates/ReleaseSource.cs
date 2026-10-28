namespace PowerTrove.Application.Updates;

public static class ReleaseSource
{
    public const string Repository = OwnerChannelConfiguration.Repository;
    public const string ApiBase = "https://api.github.com/repos/" + Repository + "/releases";
    private const string PagePrefix = "/" + Repository + "/releases/tag/";

    /// <summary>Only a canonical release-tag page for this repository can reach the launcher.</summary>
    public static bool TryValidatePage(Uri? value, out Uri? canonical)
    {
        canonical = null;
        if (value is null || !value.IsAbsoluteUri || value.Scheme != Uri.UriSchemeHttps ||
            !value.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || !value.IsDefaultPort ||
            value.UserInfo.Length != 0 || value.Query.Length != 0 || value.Fragment.Length != 0 ||
            !value.AbsolutePath.StartsWith(PagePrefix, StringComparison.Ordinal)) return false;
        string tag = Uri.UnescapeDataString(value.AbsolutePath[PagePrefix.Length..]);
        if (!ReleaseVersion.TryParse(tag, out _)) return false;
        canonical = ForTag(tag);
        return true;
    }

    public static Uri ForTag(string tag)
    {
        if (!ReleaseVersion.TryParse(tag, out _)) throw new ArgumentException("Invalid release version.", nameof(tag));
        return new Uri("https://github.com" + PagePrefix + Uri.EscapeDataString(tag));
    }
}

public enum ReleaseCheckStatus { Available, UpToDate, NoReleases, Offline, TimedOut, RateLimited, InvalidData, Unavailable, Busy }
public sealed record ReleaseCandidate(ReleaseVersion Version, Uri Page, ReleaseInstallerAsset? Installer = null);
public sealed record ReleaseInstallerAsset(Uri Download, long Size, string Sha256);

public enum ReleaseInstallStatus { Started, InvalidData, Offline, TimedOut, Failed, Busy }
public interface IReleaseInstaller
{
    Task<ReleaseInstallStatus> InstallAsync(ReleaseCandidate candidate, IProgress<int>? progress = null,
        CancellationToken cancellationToken = default);
}
public interface IReleaseInstallerLauncher
{
    Task<bool> StartAsync(string installerPath, CancellationToken cancellationToken = default);
}
public sealed record ReleaseCheckResult(ReleaseCheckStatus Status, ReleaseCandidate? Candidate = null);

public interface IReleaseDiscovery
{
    Task<ReleaseCheckResult> CheckAsync(ReleaseVersion installedVersion, CancellationToken cancellationToken = default);
}

public interface IReleasePageLauncher
{
    Task<bool> OpenAsync(Uri page, CancellationToken cancellationToken = default);
}
