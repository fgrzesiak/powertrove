using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PowerTrove.Application.Configuration;
using Xunit;

namespace PowerTrove.Application.Tests.Configuration;

public class SettingsServiceTests : IDisposable
{
    private readonly string _tempFilePath;

    public SettingsServiceTests()
    {
        _tempFilePath = Path.Combine(Path.GetTempPath(), $"settings_{Guid.NewGuid()}.json");
    }

    public void Dispose()
    {
        if (File.Exists(_tempFilePath))
        {
            File.Delete(_tempFilePath);
        }

        // Clean up backups
        var dir = Path.GetDirectoryName(_tempFilePath);
        var patternCorrupt = Path.GetFileName(_tempFilePath) + ".corrupt.*";
        foreach (var file in Directory.GetFiles(dir!, patternCorrupt))
        {
            File.Delete(file);
        }
        var patternPreMigration = Path.GetFileName(_tempFilePath) + ".pre-migration*";
        foreach (var file in Directory.GetFiles(dir!, patternPreMigration))
        {
            File.Delete(file);
        }
        var patternTemp = Path.GetFileName(_tempFilePath) + ".tmp";
        foreach (var file in Directory.GetFiles(dir!, patternTemp))
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void LoadSettings_FileDoesNotExist_ReturnsDefaultSettings()
    {
        var service = new SettingsService(_tempFilePath);
        Assert.NotNull(service.Settings);
        Assert.Equal(UserSettings.CurrentVersion, service.Settings.Version);
        Assert.Empty(service.Settings.PinnedActions);
        Assert.Empty(service.Settings.RecentActions);
        Assert.Empty(service.Settings.ActionPreferences);
    }

    [Fact]
    public async Task PinAction_SavesAndPersists()
    {
        var service = new SettingsService(_tempFilePath);
        service.PinAction("test.action");
        await service.SaveAsync(); // Ensure background save finishes

        var service2 = new SettingsService(_tempFilePath);
        Assert.Contains("test.action", service2.Settings.PinnedActions);
    }

    [Fact]
    public async Task UnpinAction_RemovesAndPersists()
    {
        var service = new SettingsService(_tempFilePath);
        service.PinAction("test.action");
        await service.SaveAsync();
        service.UnpinAction("test.action");
        await service.SaveAsync();

        var service2 = new SettingsService(_tempFilePath);
        Assert.DoesNotContain("test.action", service2.Settings.PinnedActions);
    }

    [Fact]
    public async Task RecordActionExecution_IncrementsScoreAndPersists()
    {
        var service = new SettingsService(_tempFilePath);
        service.RecordActionExecution("run.action");
        service.RecordActionExecution("run.action");
        await service.SaveAsync();

        var service2 = new SettingsService(_tempFilePath);
        Assert.True(service2.Settings.RecentActions.ContainsKey("run.action"));
        Assert.Equal(2, service2.Settings.RecentActions["run.action"]);
    }

    [Fact]
    public async Task SetActionPreference_SavesAndPersists()
    {
        var service = new SettingsService(_tempFilePath);
        service.SetActionPreference("pref.action", "useHeaders", "true");
        await service.SaveAsync();

        var service2 = new SettingsService(_tempFilePath);
        Assert.True(service2.Settings.ActionPreferences.ContainsKey("pref.action"));
        Assert.True(service2.Settings.ActionPreferences["pref.action"].ContainsKey("useHeaders"));
        Assert.Equal("true", service2.Settings.ActionPreferences["pref.action"]["useHeaders"]);
    }

    [Fact]
    public async Task SetGlobalShortcut_SavesAndPersists()
    {
        var service = new SettingsService(_tempFilePath);
        var config = new Core.Input.HotkeyConfiguration(Core.Input.KeyModifiers.Control, 0x41);
        service.SetGlobalShortcut(config);
        await service.SaveAsync();

        var service2 = new SettingsService(_tempFilePath);
        Assert.NotNull(service2.Settings.GlobalShortcut);
        Assert.Equal(Core.Input.KeyModifiers.Control, service2.Settings.GlobalShortcut.Modifiers);
        Assert.Equal((uint)0x41, service2.Settings.GlobalShortcut.Key);
    }

    [Fact]
    public async Task SetActionPreference_WithDescriptor_StoresAllButPasswords()
    {
        var service = new SettingsService(_tempFilePath);
        var descriptor = new PowerTrove.Abstractions.ActionDescriptor(
            "test.action", "Title", "Desc",
            new System.Collections.Generic.HashSet<PowerTrove.Abstractions.ContentKind>(),
            System.Array.Empty<string>(), false, false,
            Parameters: new[]
            {
                PowerTrove.Abstractions.ActionParameterDefinition.Text("Normal", "Normal", ""),
                PowerTrove.Abstractions.ActionParameterDefinition.Password("Secret", "Secret")
            }
        );

        service.SetActionPreference(descriptor, "Normal", "value");
        service.SetActionPreference(descriptor, "Secret", "p@ssword");
        await service.SaveAsync();

        var service2 = new SettingsService(_tempFilePath);
        var prefs = service2.Settings.ActionPreferences["test.action"];
        Assert.Equal("value", prefs["Normal"]);
        Assert.False(prefs.ContainsKey("Secret"));
    }

    [Fact]
    public async Task ClearRecentActions_RemovesAndPersists()
    {
        var service = new SettingsService(_tempFilePath);
        service.RecordActionExecution("run.action");
        await service.SaveAsync();

        service.ClearRecentActions();
        await service.SaveAsync();

        var service2 = new SettingsService(_tempFilePath);
        Assert.Empty(service2.Settings.RecentActions);
    }

    [Fact]
    public async Task ResetToDefaults_ResetsAndPersists()
    {
        var service = new SettingsService(_tempFilePath);
        service.PinAction("test.action");
        service.RecordActionExecution("run.action");
        service.SetActionPreference("pref.action", "useHeaders", "true");
        service.SetGlobalShortcut(new Core.Input.HotkeyConfiguration(Core.Input.KeyModifiers.Shift, 0x42));
        await service.SaveAsync();

        service.ResetToDefaults();
        await service.SaveAsync();

        var service2 = new SettingsService(_tempFilePath);
        Assert.Empty(service2.Settings.PinnedActions);
        Assert.Empty(service2.Settings.RecentActions);
        Assert.Empty(service2.Settings.ActionPreferences);
        Assert.Null(service2.Settings.GlobalShortcut);
    }


    [Fact]
    public void AddActionHistory_ShouldKeepLast50Items()
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.json");
        var service = new SettingsService(settingsPath);

        for (int i = 0; i < 60; i++)
        {
            service.AddActionHistory(new PowerTrove.Application.Configuration.ActionHistoryItem(
                $"action.{i}",
                DateTimeOffset.UtcNow,
                true,
                1,
                0
            ));
        }

        Assert.NotNull(service.Settings.ActionHistory);
        Assert.Equal(50, service.Settings.ActionHistory.Count);
        Assert.Equal("action.59", service.Settings.ActionHistory[0].ActionId);

        if (File.Exists(settingsPath))
        {
            File.Delete(settingsPath);
        }
    }
    [Fact]
    public void CorruptedSettings_RecoverSafelyWithWarning()
    {
        File.WriteAllText(_tempFilePath, "{ corrupted json... }");

        var service = new SettingsService(_tempFilePath);

        // Should recover with default settings
        Assert.NotNull(service.Settings);
        Assert.Empty(service.Settings.PinnedActions);

        // Backup should be created
        var dir = Path.GetDirectoryName(_tempFilePath);
        var pattern = Path.GetFileName(_tempFilePath) + ".corrupt.*";
        var backups = Directory.GetFiles(dir!, pattern);
        Assert.Single(backups);
    }

    [Fact]
    public void OldSchemaJson_MigratesCorrectly_AndCreatesBackup()
    {
        // Has older version
        string oldJson = @"{
            ""Version"": 1,
            ""PinnedActions"": [ ""old.action"" ],
            ""RecentActions"": { ""run.action"": 5 },
            ""ActionPreferences"": { ""pref.action"": { ""opt"": ""val"" } }
        }";
        File.WriteAllText(_tempFilePath, oldJson);

        var service = new SettingsService(_tempFilePath);

        // Verify loaded properties were preserved and version updated
        Assert.Equal(UserSettings.CurrentVersion, service.Settings.Version);
        Assert.Contains("old.action", service.Settings.PinnedActions);
        Assert.Equal(5, service.Settings.RecentActions["run.action"]);
        Assert.Equal("val", service.Settings.ActionPreferences["pref.action"]["opt"]);

        // Verify pre-migration backup was created
        var dir = Path.GetDirectoryName(_tempFilePath);
        var pattern = Path.GetFileName(_tempFilePath) + ".pre-migration-v1.*";
        var backups = Directory.GetFiles(dir!, pattern);
        Assert.Single(backups);

        // Verify it actually saved to the new file (so it contains new version)
        string newJson = File.ReadAllText(_tempFilePath);
        Assert.Contains($@"""Version"": {UserSettings.CurrentVersion}", newJson);
    }

    [Fact]
    public void FailedMigration_RecoversWithCorruptedBackup()
    {
        // Has an old version but corrupt JSON after
        string oldJson = @"{
            ""Version"": 1,
            ""PinnedActions"": [ ""old.action""
        ";
        File.WriteAllText(_tempFilePath, oldJson);

        var service = new SettingsService(_tempFilePath);

        // Should recover with default settings
        Assert.Equal(UserSettings.CurrentVersion, service.Settings.Version);
        Assert.Empty(service.Settings.PinnedActions);

        // A backup for pre-migration may have been created because version was detected before exception
        // A .corrupt backup should definitely have been created due to exception
        var dir = Path.GetDirectoryName(_tempFilePath);
        var pattern = Path.GetFileName(_tempFilePath) + ".corrupt.*";
        var backups = Directory.GetFiles(dir!, pattern);
        Assert.Single(backups);
    }

    [Fact]
    public async Task InterruptedWrite_LeavesOriginalFileIntact()
    {
        // Initial setup
        var service1 = new SettingsService(_tempFilePath);
        service1.PinAction("test.action");
        await service1.SaveAsync();

        // Simulate an interrupted save which leaves a .tmp file
        string tmpFile = _tempFilePath + ".tmp";
        string corruptJson = "{ half-written... }";
        File.WriteAllText(tmpFile, corruptJson);

        // Original file is completely fine, should load
        var service2 = new SettingsService(_tempFilePath);
        Assert.Equal(UserSettings.CurrentVersion, service2.Settings.Version);
        Assert.Contains("test.action", service2.Settings.PinnedActions);

        // Normal save should overwrite the broken .tmp file
        service2.PinAction("another.action");
        await service2.SaveAsync();

        // Ensure everything is correct
        var service3 = new SettingsService(_tempFilePath);
        Assert.Contains("test.action", service3.Settings.PinnedActions);
        Assert.Contains("another.action", service3.Settings.PinnedActions);

        // The .tmp file should no longer exist (since File.Move cleans it up, unless it failed)
        Assert.False(File.Exists(tmpFile));
    }

    [Fact]
    public async Task MarkFirstRunSeen_SetsHasSeenFirstRunToTrueAndPersists()
    {
        var service = new SettingsService(_tempFilePath);
        Assert.False(service.Settings.HasSeenFirstRun);

        service.MarkFirstRunSeen();
        await service.SaveAsync();

        var service2 = new SettingsService(_tempFilePath);
        Assert.True(service2.Settings.HasSeenFirstRun);
    }

    [Fact]
    public void LoadSettings_NewerSchema_ReturnsDefaultsAndIsReadOnly()
    {
        // Has newer version
        string newerJson = $@"{{
            ""Version"": {UserSettings.CurrentVersion + 1},
            ""PinnedActions"": [ ""new.action"" ],
            ""UnknownField"": ""Should be preserved""
        }}";
        File.WriteAllText(_tempFilePath, newerJson);

        var service = new SettingsService(_tempFilePath);

        // Verify loaded properties are defaults and it's marked readonly
        Assert.True(service.IsReadOnly);
        Assert.Equal(UserSettings.CurrentVersion, service.Settings.Version);
        Assert.Empty(service.Settings.PinnedActions);
    }

    [Fact]
    public async Task SaveAsync_WhenReadOnly_DoesNotModifyFile()
    {
        string newerJson = $@"{{
            ""Version"": {UserSettings.CurrentVersion + 1},
            ""PinnedActions"": [ ""new.action"" ],
            ""UnknownField"": ""Should be preserved""
        }}";
        File.WriteAllText(_tempFilePath, newerJson);

        var service = new SettingsService(_tempFilePath);
        Assert.True(service.IsReadOnly);

        // Attempt to save
        await service.SaveAsync();

        // The file should remain unchanged byte-for-byte
        string currentJson = File.ReadAllText(_tempFilePath);
        Assert.Equal(newerJson, currentJson);
    }

    [Fact]
    public void Mutators_WhenReadOnly_DoNotPersistChanges()
    {
        string newerJson = $@"{{
            ""Version"": {UserSettings.CurrentVersion + 1},
            ""PinnedActions"": [ ""new.action"" ],
            ""UnknownField"": ""Should be preserved""
        }}";
        File.WriteAllText(_tempFilePath, newerJson);

        var service = new SettingsService(_tempFilePath);
        Assert.True(service.IsReadOnly);

        // Try mutators that internally call Save()
        service.PinAction("test.pin");
        service.RecordActionExecution("test.recent");
        service.SetActionPreference("test.action", "key", "val");
        service.SetGlobalShortcut(new Core.Input.HotkeyConfiguration(Core.Input.KeyModifiers.Control, 0x41));
        service.AddImageExportPreset(new SavedImageExportPreset("id", "title", "desc", "png", 100, null, null, false, false));
        service.RemoveImageExportPreset("id");
        service.ClearRecentActions();
        service.ResetToDefaults();
        service.MarkFirstRunSeen();

        // Check file remains untouched
        string currentJson = File.ReadAllText(_tempFilePath);
        Assert.Equal(newerJson, currentJson);
    }

    [Fact]
    public void LegacyOverwritePreference_LoadsSafelyAndRemainsInert()
    {
        // Issue #187: Ensure existing legacy preferences like overwriteFiles=true
        // parse successfully but remain inert inside the SettingsService without
        // throwing or modifying the file.
        string legacyJson = $@"{{
            ""Version"": {UserSettings.CurrentVersion},
            ""ActionPreferences"": {{
                ""powertrove.general.output"": {{
                    ""overwriteFiles"": ""true""
                }}
            }}
        }}";
        File.WriteAllText(_tempFilePath, legacyJson);

        var service = new SettingsService(_tempFilePath);

        Assert.Equal(UserSettings.CurrentVersion, service.Settings.Version);
        Assert.True(service.Settings.ActionPreferences.ContainsKey("powertrove.general.output"));
        Assert.True(service.Settings.ActionPreferences["powertrove.general.output"].ContainsKey("overwriteFiles"));
        Assert.Equal("true", service.Settings.ActionPreferences["powertrove.general.output"]["overwriteFiles"]);
    }

    [Fact]
    public void V3Migration_ShouldPreserveDataAndInitializeSelectionUsage()
    {
        string v2Json = @"{
            ""Version"": 2,
            ""PinnedActions"": [ ""old.action"" ]
        }";
        File.WriteAllText(_tempFilePath, v2Json);

        var service = new SettingsService(_tempFilePath);

        Assert.Equal(3, service.Settings.Version);
        Assert.Contains("old.action", service.Settings.PinnedActions);
        Assert.NotNull(service.Settings.SelectionUsage);
        Assert.Empty(service.Settings.SelectionUsage);
    }

    [Fact]
    public void RecordSelectionUsage_ValidKey_ShouldRecord()
    {
        var service = new SettingsService(_tempFilePath);
        var now = DateTimeOffset.UtcNow;
        service.RecordSelectionUsage("files:pdf:one", "action1", now);

        Assert.NotNull(service.Settings.SelectionUsage);
        Assert.True(service.Settings.SelectionUsage.ContainsKey("files:pdf:one"));
        Assert.True(service.Settings.SelectionUsage["files:pdf:one"].ContainsKey("action1"));
        Assert.Equal(1, service.Settings.SelectionUsage["files:pdf:one"]["action1"].Count);
        Assert.Equal(now, service.Settings.SelectionUsage["files:pdf:one"]["action1"].LastUsedUtc);
    }

    [Fact]
    public void RecordSelectionUsage_InvalidKey_ShouldIgnore()
    {
        var service = new SettingsService(_tempFilePath);
        service.RecordSelectionUsage("invalid:key", "action1", DateTimeOffset.UtcNow);
        service.RecordSelectionUsage("files:pdf:none", "action2", DateTimeOffset.UtcNow);

        Assert.Null(service.Settings.SelectionUsage);
    }

    [Fact]
    public void RecordSelectionUsage_ShouldUpdateExisting()
    {
        var service = new SettingsService(_tempFilePath);
        var time1 = DateTimeOffset.UtcNow.AddDays(-1);
        var time2 = DateTimeOffset.UtcNow;

        service.RecordSelectionUsage("files:image:few", "action1", time1);
        service.RecordSelectionUsage("files:image:few", "action1", time2);

        Assert.Equal(2, service.Settings.SelectionUsage["files:image:few"]["action1"].Count);
        Assert.Equal(time2, service.Settings.SelectionUsage["files:image:few"]["action1"].LastUsedUtc);
    }

    [Fact]
    public void RecordSelectionUsage_ShouldEnforce30ActionsPerKey()
    {
        var service = new SettingsService(_tempFilePath);
        var now = DateTimeOffset.UtcNow;

        for (int i = 0; i < 30; i++)
        {
            service.RecordSelectionUsage("files:pdf:several", $"action_{i}", now.AddDays(-30 + i));
        }

        service.RecordSelectionUsage("files:pdf:several", "action_new", now);

        var actions = service.Settings.SelectionUsage["files:pdf:several"];
        Assert.Equal(30, actions.Count);
        Assert.True(actions.ContainsKey("action_new"));
        Assert.False(actions.ContainsKey("action_0"));
    }

    [Fact]
    public void RecordSelectionUsage_ShouldEnforce200Keys()
    {
        var service = new SettingsService(_tempFilePath);
        var now = DateTimeOffset.UtcNow;

        for (int i = 0; i < 200; i++)
        {
            string key = $"files:{new string((char)('a' + (i % 26)), 1 + i / 26)}:one";
            service.RecordSelectionUsage(key, $"action", now.AddMinutes(i));
        }

        Assert.Equal(200, service.Settings.SelectionUsage.Count);

        string firstKey = "files:a:one";
        Assert.True(service.Settings.SelectionUsage.ContainsKey(firstKey));

        string newKey = "files:pdf:many";
        service.RecordSelectionUsage(newKey, "action", now.AddMinutes(200));

        Assert.Equal(200, service.Settings.SelectionUsage.Count);
        Assert.True(service.Settings.SelectionUsage.ContainsKey(newKey));
        Assert.False(service.Settings.SelectionUsage.ContainsKey(firstKey));
    }

    [Fact]
    public void ClearSelectionUsage_ShouldRemoveAllEntries()
    {
        var service = new SettingsService(_tempFilePath);
        service.RecordSelectionUsage("files:pdf:one", "action1", DateTimeOffset.UtcNow);

        service.ClearSelectionUsage();

        Assert.NotNull(service.Settings.SelectionUsage);
        Assert.Empty(service.Settings.SelectionUsage);
    }

    [Fact]
    public async System.Threading.Tasks.Task SerializedSettings_ShouldContainSelectionUsage()
    {
        var service = new SettingsService(_tempFilePath);
        var now = DateTimeOffset.UtcNow;
        service.RecordSelectionUsage("files:pdf:one", "action1", now);

        await service.SaveAsync();

        string json = File.ReadAllText(_tempFilePath);
        Assert.Contains("SelectionUsage", json);
        Assert.Contains("files:pdf:one", json);
        Assert.Contains("action1", json);
        Assert.Contains("Count", json);
        Assert.Contains("LastUsedUtc", json);
    }


    [Fact]
    public void LoadSettings_MalformedSelectionUsage_DiscardsInvalidEntriesAndKeepsValidSettings()
    {
        string json = @"{
            ""Version"": 3,
            ""PinnedActions"": [""action1""],
            ""SelectionUsage"": {
                ""files:pdf:one"": null,
                ""invalid_key"": { ""action2"": { ""Count"": 1, ""LastUsedUtc"": ""2023-01-01T00:00:00Z"" } },
                ""files:image:few"": { ""action3"": null, ""action4"": { ""Count"": 5, ""LastUsedUtc"": ""2023-01-01T00:00:00Z"" } },
                ""files:table:none"": null
            }
        }";
        System.IO.File.WriteAllText(_tempFilePath, json);

        var service = new SettingsService(_tempFilePath);

        // Should not reset unrelated settings
        Assert.Contains("action1", service.Settings.PinnedActions);

        // Should discard null dictionary, invalid key, and null entry
        var usage = service.Settings.SelectionUsage;
        Assert.NotNull(usage);
        Assert.False(usage.ContainsKey("files:pdf:one"));
        Assert.False(usage.ContainsKey("invalid_key"));
        Assert.False(usage.ContainsKey("files:table:none"));

        Assert.True(usage.ContainsKey("files:image:few"));
        Assert.False(usage["files:image:few"].ContainsKey("action3"));
        Assert.True(usage["files:image:few"].ContainsKey("action4"));
    }

    [Fact]
    public void LoadSettings_OversizedSelectionUsage_TrimsToLimitsOnLoad()
    {
        var now = DateTimeOffset.UtcNow;
        var usageObj = new System.Collections.Generic.Dictionary<string, object>();

        // Add 210 signatures
        for (int i = 0; i < 210; i++)
        {
            var actions = new System.Collections.Generic.Dictionary<string, object>();

            int actionCount = i == 0 ? 40 : 1;

            for (int j = 0; j < actionCount; j++)
            {
                actions[$"action_{j}"] = new { Count = j + 1, LastUsedUtc = now.AddDays(-100 + j) };
            }

            // Generate valid keys
            string key = $"files:{new string((char)('a' + (i % 26)), 1 + i / 26)}:one";
            if (i == 0) key = "files:pdf:several";

            usageObj[key] = actions;
        }

        var root = new { Version = 3, SelectionUsage = usageObj };
        string json = System.Text.Json.JsonSerializer.Serialize(root);
        System.IO.File.WriteAllText(_tempFilePath, json);

        var service = new SettingsService(_tempFilePath);
        var usage = service.Settings.SelectionUsage;

        Assert.NotNull(usage);
        Assert.Equal(200, usage.Count); // Should be trimmed to 200

        Assert.True(usage.ContainsKey("files:pdf:several"));
        Assert.Equal(30, usage["files:pdf:several"].Count); // Should be trimmed to 30

        // Ensure it saves and reloads identically
        service.Save();
        var service2 = new SettingsService(_tempFilePath);
        var usage2 = service2.Settings.SelectionUsage;
        Assert.Equal(200, usage2.Count);
        Assert.Equal(30, usage2["files:pdf:several"].Count);
    }

}
