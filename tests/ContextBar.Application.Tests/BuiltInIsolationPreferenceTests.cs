using ContextBar.Application.Configuration;
using Xunit;

namespace ContextBar.Application.Tests;

public sealed class BuiltInIsolationPreferenceTests
{
    [Fact]
    public void Setting_IsOffByDefault_PersistsAndResets()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ContextBar-Isolation-" + Guid.NewGuid().ToString("N"));
        string file = Path.Combine(dir, "settings.json");
        try
        {
            ISettingsService settings = new SettingsService(file);
            Assert.False(settings.Settings.RunFileToolsInSeparateProcess);
            settings.SetRunFileToolsInSeparateProcess(true);
            Assert.True(new SettingsService(file).Settings.RunFileToolsInSeparateProcess);
            settings.ResetToDefaults();
            Assert.False(settings.Settings.RunFileToolsInSeparateProcess);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }
}
