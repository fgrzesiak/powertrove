using PowerTrove.Application.Configuration;

namespace PowerTrove.Application.Tests;

public sealed class DesktopDistributionTests
{
    [Fact]
    public void PackagedInstallation_OwnsSeparateStateAndCannotMaintainTheInstaller()
    {
        string userData = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "generated-user"));
        string cache = Path.Combine(userData, "Packages", "generated-family", "LocalCache");
        var unpackaged = DesktopDistribution.Create(false, userData);
        var packaged = DesktopDistribution.Create(true, userData, cache);
        Assert.Equal(Path.Combine(userData, "PowerTrove"), unpackaged.UserRoot);
        Assert.Equal(Path.Combine(cache, "PowerTrove"), packaged.UserRoot);
        Assert.NotEqual(unpackaged.UserRoot, packaged.UserRoot);
        Assert.NotEqual(unpackaged.InstanceKey, packaged.InstanceKey);
        Assert.False(packaged.AllowsInstallerUpdates);
        Assert.False(packaged.AllowsSetupMaintenance);
        Assert.True(unpackaged.AllowsInstallerUpdates);
        Assert.True(unpackaged.AllowsSetupMaintenance);
    }

    [Fact]
    public void MissingOrRelativePackageStorage_NeverFallsBackToTheExistingInstallation()
    {
        Assert.Throws<ArgumentNullException>(() => DesktopDistribution.Create(true, Path.GetTempPath()));
        Assert.Throws<ArgumentException>(() => DesktopDistribution.Create(true, Path.GetTempPath(), "relative"));
    }
}
