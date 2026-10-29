using System.Text.Json;
using PowerTrove.Application.Configuration;

namespace PowerTrove.Application.Tests;

public sealed class UpdatePreferencesTests
{
    [Fact]
    public void AutomaticPackChecks_DefaultOn_AndOptOutIsIndependentOfHostChecks()
    {
        string directory = Path.Combine(Path.GetTempPath(), "PowerTrove-pack-update-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "settings.json");
            File.WriteAllText(path, JsonSerializer.Serialize(UserSettings.CreateDefault()).Replace(",\"CheckForPackUpdatesAutomatically\":true", ""));
            var settings = new SettingsService(path); Assert.True(settings.Settings.CheckForPackUpdatesAutomatically);
            settings.SetCheckForPackUpdatesAutomatically(false);
            settings = new SettingsService(path); Assert.False(settings.Settings.CheckForPackUpdatesAutomatically);
            Assert.True(settings.Settings.CheckForUpdatesOnStartup);
            settings.SetCheckForUpdatesOnStartup(false); settings.SetCheckForPackUpdatesAutomatically(true);
            settings = new SettingsService(path); Assert.True(settings.Settings.CheckForPackUpdatesAutomatically);
            Assert.False(settings.Settings.CheckForUpdatesOnStartup);
            settings.ResetToDefaults(); Assert.True(settings.Settings.CheckForPackUpdatesAutomatically);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void ExistingSettings_DefaultToStartupChecks_AndOptOutSurvivesReload()
    {
        string directory = Path.Combine(Path.GetTempPath(), "PowerTrove-update-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "settings.json");
            string old = JsonSerializer.Serialize(UserSettings.CreateDefault());
            old = old.Replace(",\"CheckForUpdatesOnStartup\":true", "");
            File.WriteAllText(path, old);
            var service = new SettingsService(path); Assert.True(service.Settings.CheckForUpdatesOnStartup);
            service.SetCheckForUpdatesOnStartup(false);
            Assert.False(new SettingsService(path).Settings.CheckForUpdatesOnStartup);
            service.ResetToDefaults(); Assert.True(service.Settings.CheckForUpdatesOnStartup);
        }
        finally { Directory.Delete(directory, true); }
    }
}
