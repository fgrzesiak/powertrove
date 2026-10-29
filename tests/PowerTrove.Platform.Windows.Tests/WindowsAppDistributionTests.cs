using PowerTrove.Platform.Windows.Deployment;

namespace PowerTrove.Platform.Windows.Tests;

public sealed class WindowsAppDistributionTests
{
    [Fact]
    public void ActualUnpackagedTestProcess_ResolvesTheExistingDataRootWithoutCreatingIt()
    {
        var distribution = WindowsAppDistribution.Current;
        Assert.False(distribution.IsPackaged);
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PowerTrove"), distribution.UserRoot);
        Assert.Same(distribution, WindowsAppDistribution.Current);
    }
}
