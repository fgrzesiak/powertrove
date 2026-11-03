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
        foreach (string name in new[] { "Images", "Media", "Office", "Tables", "Data", "EInvoice", "Markdown" })
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
    public void UserRecipes_AreOnlyCompiledIntoDebugBuilds_UnlessEnabledExplicitly()
    {
        // Release workflows publish Release builds, so features in development stay out of releases.
        string root = PackFactoriesTests.FindRoot();
        var doc = XDocument.Load(Path.Combine(root, "src/PowerTrove.Desktop/PowerTrove.Desktop.csproj"));
        var property = Assert.Single(doc.Descendants("PowerTroveUserRecipes"));
        Assert.Equal("true", property.Value);
        Assert.Equal("'$(PowerTroveUserRecipes)' == '' and '$(Configuration)' == 'Debug'", (string?)property.Attribute("Condition"));
        var define = Assert.Single(doc.Descendants("DefineConstants").Where(e => e.Value.Contains("POWERTROVE_USER_RECIPES")));
        Assert.Equal("'$(PowerTroveUserRecipes)' == 'true'", (string?)define.Attribute("Condition"));
        string features = File.ReadAllText(Path.Combine(root, "src/PowerTrove.Desktop/Features.cs"));
        Assert.Contains("#if POWERTROVE_USER_RECIPES", features);
        string app = File.ReadAllText(Path.Combine(root, "src/PowerTrove.Desktop/App.xaml.cs"));
        Assert.Matches(@"if \(Features\.UserRecipes\)\s+actionRegistry\.Register\(new PowerTrove\.Application\.Commands\.CreateRecipeAction", app);
        Assert.Contains("!Features.UserRecipes", app);
    }

    [Fact]
    public void CoreSource_LinksNoPack_AndBaseSourceLinksOnlyFilesAndPdf()
    {
        string root = PackFactoriesTests.FindRoot();
        string core = File.ReadAllText(Path.Combine(root, "src/PowerTrove.Desktop/BuiltInActionPacks.cs"));
        string @base = File.ReadAllText(Path.Combine(root, "src/PowerTrove.Desktop/BuiltInActionPacks.Base.cs"));
        Assert.DoesNotContain("PowerTrove.Pack.", core, StringComparison.Ordinal);
        foreach (string name in new[] { "Images", "Media", "Office", "Tables", "Data", "EInvoice", "Markdown" })
            Assert.DoesNotContain("PowerTrove.Pack." + name, @base, StringComparison.Ordinal);
        Assert.Contains("new FilesPackFactory()", @base);
        Assert.Contains("new PdfPackFactory(", @base);
        // Desktop windows and the app shell use only host contracts, never pack types.
        foreach (string file in Directory.GetFiles(Path.Combine(root, "src/PowerTrove.Desktop"), "*.cs")
            .Where(f => !Path.GetFileName(f).StartsWith("BuiltInActionPacks.", StringComparison.Ordinal) || Path.GetFileName(f) == "BuiltInActionPacks.cs"))
            Assert.DoesNotContain("PowerTrove.Pack.", File.ReadAllText(file), StringComparison.Ordinal);
    }
}
