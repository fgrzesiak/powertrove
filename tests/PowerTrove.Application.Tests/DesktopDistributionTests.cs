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
    public void TestAndProductionPackageFamilies_HaveDistinctStableActivationChannels()
    {
        string local = Path.GetTempPath();
        var test = DesktopDistribution.Create(true, local, Path.Combine(local, "Packages", "test-family", "LocalCache"));
        var production = DesktopDistribution.Create(true, local, Path.Combine(local, "Packages", "production-family", "LocalCache"));
        Assert.NotEqual(test.InstanceKey, production.InstanceKey);
        Assert.InRange(test.InstanceKey.Length, 1, 64);
        Assert.All(test.InstanceKey, c => Assert.True(char.IsAsciiLetterOrDigit(c)));
        Assert.Equal(test.InstanceKey, DesktopDistribution.Create(true, local,
            Path.Combine(local, "Packages", "test-family", "LocalCache")).InstanceKey);
        Assert.DoesNotContain(test.UserRoot, test.InstanceKey);
    }

    [Fact]
    public void MissingOrRelativePackageStorage_NeverFallsBackToTheExistingInstallation()
    {
        Assert.Throws<ArgumentNullException>(() => DesktopDistribution.Create(true, Path.GetTempPath()));
        Assert.Throws<ArgumentException>(() => DesktopDistribution.Create(true, Path.GetTempPath(), "relative"));
    }
}
