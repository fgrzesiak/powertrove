using System.Xml.Linq;
using Xunit;

namespace PowerTrove.Architecture.Tests;

public sealed class DesktopDistributionProfileTests
{
    [Fact]
    public void ProfileConditions_OnOptionalReferences_MatchCompileSelection()
    {
        string root = PackFactoriesTests.FindRoot();
        var doc = XDocument.Load(Path.Combine(root, "src/PowerTrove.Desktop/PowerTrove.Desktop.csproj"));
        var refs = doc.Descendants("ProjectReference").ToArray();
        foreach (string name in new[] { "Files", "Pdf" })
        {
            var match = Assert.Single(refs.Where(x => ((string?)x.Attribute("Include"))?.Contains("PowerTrove.Pack." + name + @"\composition\") == true));
            Assert.Null(match.Attribute("Condition"));
        }
        foreach (string name in new[] { "Images", "Media", "Office", "Tables", "Data" })
        {
            var match = Assert.Single(refs.Where(x => ((string?)x.Attribute("Include"))?.Contains("PowerTrove.Pack." + name + @"\composition\") == true));
            Assert.Equal("'$(PowerTroveProfile)' == 'Full'", (string?)match.Attribute("Condition"));
        }
        Assert.Contains(doc.Descendants("Compile"), e =>
            (string?)e.Attribute("Remove") == "BuiltInActionPacks.Full.cs" &&
            (string?)e.Attribute("Condition") == "'$(PowerTroveProfile)' == 'Base'");
        Assert.Contains(doc.Descendants("PowerTroveProfile"), e =>
            e.Value == "Full" && (string?)e.Attribute("Condition") == "'$(PowerTroveProfile)' == ''");
    }

    [Fact]
    public void BaseSource_DoesNotLinkOptionalFactories()
    {
        string root = PackFactoriesTests.FindRoot();
        string common = File.ReadAllText(Path.Combine(root, "src/PowerTrove.Desktop/BuiltInActionPacks.cs"));
        foreach (string name in new[] { "Images", "Media", "Office", "Tables", "Data" })
            Assert.DoesNotContain("PowerTrove.Pack." + name, common, StringComparison.Ordinal);
        Assert.Contains("new FilesPackFactory()", common);
        Assert.Contains("new PdfPackFactory(", common);
    }
}
