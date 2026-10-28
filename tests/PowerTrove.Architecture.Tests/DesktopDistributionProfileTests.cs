using System.Xml.Linq;
using Xunit;

namespace PowerTrove.Architecture.Tests;

public sealed class DesktopDistributionProfileTests
{
    [Fact]
    public void ProfileConditions_OnPackReferences_MatchCompileSelection_AndCoreIsTheDefault()
    {
        string root = PackFactoriesTests.FindRoot();
        var doc = XDocument.Load(Path.Combine(root, "src/PowerTrove.Desktop/PowerTrove.Desktop.csproj"));
        var refs = doc.Descendants("ProjectReference").ToArray();
        foreach (string name in new[] { "Files", "Pdf" })
        {
            var match = Assert.Single(refs.Where(x => ((string?)x.Attribute("Include"))?.Contains("PowerTrove.Pack." + name + @"\composition\") == true));
            Assert.Equal("'$(PowerTroveProfile)' != 'Core'", (string?)match.Attribute("Condition"));
        }
        foreach (string name in new[] { "Images", "Media", "Office", "Tables", "Data", "EInvoice" })
        {
            var match = Assert.Single(refs.Where(x => ((string?)x.Attribute("Include"))?.Contains("PowerTrove.Pack." + name + @"\composition\") == true));
            Assert.Equal("'$(PowerTroveProfile)' == 'Full'", (string?)match.Attribute("Condition"));
        }
        Assert.Contains(doc.Descendants("Compile"), e =>
            (string?)e.Attribute("Remove") == "BuiltInActionPacks.Base.cs" && (string?)e.Attribute("Condition") == "'$(PowerTroveProfile)' == 'Core'");
        Assert.Contains(doc.Descendants("Compile"), e =>
            (string?)e.Attribute("Remove") == "BuiltInActionPacks.Full.cs" && (string?)e.Attribute("Condition") == "'$(PowerTroveProfile)' != 'Full'");
        Assert.Contains(doc.Descendants("PowerTroveProfile"), e =>
            e.Value == "Core" && (string?)e.Attribute("Condition") == "'$(PowerTroveProfile)' == ''");
    }

    [Fact]
    public void CoreSource_LinksNoPack_AndBaseSourceLinksOnlyFilesAndPdf()
    {
        string root = PackFactoriesTests.FindRoot();
        string core = File.ReadAllText(Path.Combine(root, "src/PowerTrove.Desktop/BuiltInActionPacks.cs"));
        string @base = File.ReadAllText(Path.Combine(root, "src/PowerTrove.Desktop/BuiltInActionPacks.Base.cs"));
        Assert.DoesNotContain("PowerTrove.Pack.", core, StringComparison.Ordinal);
        foreach (string name in new[] { "Images", "Media", "Office", "Tables", "Data", "EInvoice" })
            Assert.DoesNotContain("PowerTrove.Pack." + name, @base, StringComparison.Ordinal);
        Assert.Contains("new FilesPackFactory()", @base);
        Assert.Contains("new PdfPackFactory(", @base);
        // Desktop windows and the app shell use only host contracts, never pack types.
        foreach (string file in Directory.GetFiles(Path.Combine(root, "src/PowerTrove.Desktop"), "*.cs")
            .Where(f => !Path.GetFileName(f).StartsWith("BuiltInActionPacks.", StringComparison.Ordinal) || Path.GetFileName(f) == "BuiltInActionPacks.cs"))
            Assert.DoesNotContain("PowerTrove.Pack.", File.ReadAllText(file), StringComparison.Ordinal);
    }
}
