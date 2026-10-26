using System.Xml.Linq;
using Xunit;

namespace ContextBar.Architecture.Tests;

public sealed class DesktopDistributionProfileTests
{
    [Fact]
    public void ProfileConditions_OnOptionalReferences_MatchCompileSelection()
    {
        string root = PackFactoriesTests.FindRoot();
        var doc = XDocument.Load(Path.Combine(root, "src/ContextBar.Desktop/ContextBar.Desktop.csproj"));
        var refs = doc.Descendants("ProjectReference").ToArray();
        foreach (string name in new[] { "Files", "Pdf" })
        {
            var match = Assert.Single(refs.Where(x => ((string?)x.Attribute("Include"))?.Contains("ContextBar.Pack." + name + @"\composition\") == true));
            Assert.Null(match.Attribute("Condition"));
        }
        foreach (string name in new[] { "Images", "Media", "Office", "Tables", "Data" })
        {
            var match = Assert.Single(refs.Where(x => ((string?)x.Attribute("Include"))?.Contains("ContextBar.Pack." + name + @"\composition\") == true));
            Assert.Equal("'$(ContextBarProfile)' == 'Full'", (string?)match.Attribute("Condition"));
        }
        Assert.Contains(doc.Descendants("Compile"), e =>
            (string?)e.Attribute("Remove") == "BuiltInActionPacks.Full.cs" &&
            (string?)e.Attribute("Condition") == "'$(ContextBarProfile)' == 'Base'");
        Assert.Contains(doc.Descendants("ContextBarProfile"), e =>
            e.Value == "Full" && (string?)e.Attribute("Condition") == "'$(ContextBarProfile)' == ''");
    }

    [Fact]
    public void BaseSource_DoesNotLinkOptionalFactories()
    {
        string root = PackFactoriesTests.FindRoot();
        string common = File.ReadAllText(Path.Combine(root, "src/ContextBar.Desktop/BuiltInActionPacks.cs"));
        foreach (string name in new[] { "Images", "Media", "Office", "Tables", "Data" })
            Assert.DoesNotContain("ContextBar.Pack." + name, common, StringComparison.Ordinal);
        Assert.Contains("new FilesPackFactory()", common);
        Assert.Contains("new PdfPackFactory(", common);
    }
}
