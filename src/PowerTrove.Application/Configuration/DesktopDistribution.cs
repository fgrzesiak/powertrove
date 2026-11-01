namespace PowerTrove.Application.Configuration;

/// <summary>Installation ownership. Packaged installs never maintain an unpackaged installation.</summary>
public sealed record DesktopDistribution(bool IsPackaged, string UserRoot)
{
    public bool AllowsInstallerUpdates => !IsPackaged;
    public bool AllowsSetupMaintenance => !IsPackaged;
    // LocalCache is stable across package upgrades and distinct for every package family.
    // Test and production packages must not forward activation into each other.
    public string InstanceKey => IsPackaged ? "PowerTroveStore" + Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            Path.GetFullPath(UserRoot).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant())))[..32] : "PowerTrove";

    public static DesktopDistribution Create(bool isPackaged, string localAppData, string? packageCache = null)
    {
        string parent = isPackaged ? packageCache ?? throw new ArgumentNullException(nameof(packageCache)) : localAppData;
        if (!Path.IsPathFullyQualified(parent)) throw new ArgumentException("An absolute user-data root is required.");
        return new(isPackaged, Path.Combine(parent, "PowerTrove"));
    }
}
