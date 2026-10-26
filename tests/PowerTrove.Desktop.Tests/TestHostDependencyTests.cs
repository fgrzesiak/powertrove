using System.Text.Json;

namespace PowerTrove.Desktop.Tests;

public sealed class TestHostDependencyTests
{
    [Fact]
    public void ManagedCompositionHost_HasNoWinUiExecutableOrRuntimeDependency()
    {
        string manifest = Path.ChangeExtension(typeof(TestHostDependencyTests).Assembly.Location, ".deps.json");
        using var document = JsonDocument.Parse(File.ReadAllText(manifest));
        var dependencies = document.RootElement.GetProperty("libraries").EnumerateObject()
            .Select(entry => entry.Name.Split('/')[0]).ToArray();

        Assert.NotEmpty(dependencies);
        Assert.DoesNotContain(dependencies, name =>
            name.Equals("PowerTrove.Desktop", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("PowerTrove", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Microsoft.WindowsAppSDK", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Microsoft.WindowsAppRuntime", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Microsoft.UI.Xaml", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(typeof(TestHostDependencyTests).Assembly.GetReferencedAssemblies(), reference =>
            reference.Name == "PowerTrove" || reference.Name?.StartsWith("Microsoft.UI.", StringComparison.Ordinal) == true);
    }
}
