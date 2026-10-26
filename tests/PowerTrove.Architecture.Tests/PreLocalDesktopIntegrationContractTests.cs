using System.Xml.Linq;
using Xunit;

namespace PowerTrove.Architecture.Tests;

public sealed class PreLocalDesktopIntegrationContractTests
{
    [Fact]
    public void WinUiHasOneExplicitElectedEntryPoint()
    {
        string root = PackFactoriesTests.FindRoot();
        string project = File.ReadAllText(Path.Combine(root, "src/PowerTrove.Desktop/PowerTrove.Desktop.csproj"));
        string program = File.ReadAllText(Path.Combine(root, "src/PowerTrove.Desktop/Program.cs"));
        string app = File.ReadAllText(Path.Combine(root, "src/PowerTrove.Desktop/App.xaml.cs"));
        Assert.Contains("DISABLE_XAML_GENERATED_MAIN", project);
        Assert.Contains("new App(elector, message)", program);
        Assert.Contains("App(IHostElector elector, ActivationMessage message)", app);
        Assert.Contains("StartListeningAsync(ReceiveActivationAsync", app);
        Assert.Contains("ExplicitSelectionValidator", app);
        Assert.DoesNotContain("TryOpenPagesFromCommandLine", app);
    }

    [Fact]
    public void ExplorerMenuBindingHasHandlersAndBilingualResources()
    {
        string root = PackFactoriesTests.FindRoot();
        string xaml = File.ReadAllText(Path.Combine(root, "src/PowerTrove.Desktop/SettingsWindow.xaml"));
        string handlers = File.ReadAllText(Path.Combine(root, "src/PowerTrove.Desktop/SettingsWindow.xaml.cs"));
        string text = File.ReadAllText(Path.Combine(root, "src/PowerTrove.Desktop/UiText.cs"));
        var en = XDocument.Load(Path.Combine(root, "src/PowerTrove.Desktop/Resources/Strings.resx"));
        var de = XDocument.Load(Path.Combine(root, "src/PowerTrove.Desktop/Resources/Strings.de.resx"));
        foreach (string name in new[] { "ExplorerAdd_Click", "ExplorerRemove_Click" })
        {
            Assert.Contains("Click=\"" + name + "\"", xaml);
            Assert.Contains("void " + name + "(", handlers);
        }
        foreach (string name in new[] { "ExplorerHeading", "ExplorerDescription", "ExplorerAdd", "ExplorerRemove",
            "ExplorerMenuLabel", "ExplorerRegistered", "ExplorerAbsent", "ExplorerIncomplete",
            "ExplorerConflict", "ExplorerUnavailable" })
        {
            Assert.Contains("string " + name + " =>", text);
            Assert.Contains(en.Descendants("data"), x => (string?)x.Attribute("name") == name);
            Assert.Contains(de.Descendants("data"), x => (string?)x.Attribute("name") == name);
        }
    }
}
