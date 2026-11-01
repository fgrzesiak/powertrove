using PowerTrove.Platform.Windows.Deployment;

namespace PowerTrove.Platform.Windows.Tests;

public sealed class WindowsAppDistributionTests
{
    [Fact]
    public void PackageFamilyActivationKey_IsAcceptedByTheRealWindowsElector()
    {
        var distribution = PowerTrove.Application.Configuration.DesktopDistribution.Create(true, Path.GetTempPath(),
            Path.Combine(Path.GetTempPath(), "generated-package-family", "LocalCache"));
        using var elector = new PowerTrove.Platform.Windows.Activation.WindowsHostElector(distribution.InstanceKey);
        // Construction validates the real adapter's key contract; do not acquire an app mutex or pipe.
    }

    [Fact]
    public void ActualUnpackagedTestProcess_ResolvesTheExistingDataRootWithoutCreatingIt()
    {
        var distribution = WindowsAppDistribution.Current;
        Assert.False(distribution.IsPackaged);
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PowerTrove"), distribution.UserRoot);
        Assert.Same(distribution, WindowsAppDistribution.Current);
    }
}
