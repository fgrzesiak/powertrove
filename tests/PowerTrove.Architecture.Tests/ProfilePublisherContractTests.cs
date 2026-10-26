using Xunit;

namespace PowerTrove.Architecture.Tests;

public sealed class ProfilePublisherContractTests
{
    [Fact]
    public void BaseProfilePublisher_GeneratesCatalogAndContainsSeparateWorker()
    {
        string root = PackFactoriesTests.FindRoot();
        string text = File.ReadAllText(Path.Combine(root, "scripts/build-profiles.ps1"));
        Assert.Contains("-p:PowerTroveProfile=$Profile", text);
        Assert.Contains("src/PowerTrove.PackHost/PowerTrove.PackHost.csproj", text);
        Assert.Contains("scripts/build-packs.ps1", text);
        Assert.Contains("-DryRun", text);
        Assert.Contains("catalog.json", text);
    }
}
