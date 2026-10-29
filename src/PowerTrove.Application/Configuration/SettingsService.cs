using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using PowerTrove.Abstractions;

namespace PowerTrove.Application.Configuration;

public class SettingsService : ISettingsService, IPackVisibilitySettings, IUpdateCheckSettings, IPackMigrationSettings, IPackUpdateCheckSettings
{
    private readonly string _settingsFilePath;
    private UserSettings _settings;
    private readonly object _lock = new object();

    public bool IsReadOnly { get; private set; }

    public UserSettings Settings
    {
        get
        {
            lock (_lock)
            {
                return _settings;
            }
        }
    }

    public SettingsService(string settingsFilePath)
    {
        _settingsFilePath = settingsFilePath ?? throw new ArgumentNullException(nameof(settingsFilePath));
        HadExistingSettings = File.Exists(_settingsFilePath);
        _settings = LoadSettings(out bool needsSave);
        if (needsSave)
        {
            Save();
        }
    }

    private UserSettings LoadSettings(out bool needsSave)
    {
        needsSave = false;
        if (!File.Exists(_settingsFilePath))
        {
            return UserSettings.CreateDefault();
        }

        try
        {
            string json = File.ReadAllText(_settingsFilePath);
            int version = 0;

            try
            {
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.TryGetProperty("Version", out var versionElement) && versionElement.ValueKind == JsonValueKind.Number)
                {
                    version = versionElement.GetInt32();
                }
            }
            catch (JsonException)
            {
                // Ignored here, main catch block will handle it if it fails to deserialize
            }

            if (version > 0 && version < UserSettings.CurrentVersion)
            {
                BackupSettings($".pre-migration-v{version}");
                needsSave = true;
            }
            else if (version > UserSettings.CurrentVersion)
            {
                IsReadOnly = true;
                needsSave = false;
                return UserSettings.CreateDefault();
            }

            var settings = JsonSerializer.Deserialize<UserSettings>(json);

            if (settings == null)
            {
                throw new InvalidOperationException("Deserialized settings was null.");
            }

            // Ensure collections are not null
            var pinnedActions = settings.PinnedActions ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var recentActions = settings.RecentActions ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var actionPreferences = settings.ActionPreferences ?? new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

            // Create a normalized copy in case the JSON used a different comparer or had nulls
            var normalizedActionPreferences = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in actionPreferences)
            {
                normalizedActionPreferences[kvp.Key] = new Dictionary<string, string>(kvp.Value, StringComparer.OrdinalIgnoreCase);
            }

            var selectionUsage = new Dictionary<string, Dictionary<string, SelectionUsageEntry>>(StringComparer.OrdinalIgnoreCase);
            if (settings.SelectionUsage != null)
            {
                var now = DateTimeOffset.UtcNow;
                foreach (var kvp in settings.SelectionUsage)
                {
                    if (kvp.Value == null || !System.Text.RegularExpressions.Regex.IsMatch(kvp.Key, @"^(files:[a-z+]+:(one|few|several|many)|table|text|none)$"))
                    {
                        continue;
                    }

                    var actions = new Dictionary<string, SelectionUsageEntry>(StringComparer.OrdinalIgnoreCase);
                    foreach (var actionKvp in kvp.Value)
                    {
                        if (actionKvp.Value != null)
                        {
                            actions[actionKvp.Key] = actionKvp.Value;
                        }
                    }

                    if (actions.Count > 30)
                    {
                        var trimmedActions = new Dictionary<string, SelectionUsageEntry>(StringComparer.OrdinalIgnoreCase);
                        var orderedActions = actions.OrderByDescending(x =>
                        {
                            double ageDays = (now - x.Value.LastUsedUtc).TotalDays;
                            return x.Value.Count * Math.Pow(0.5, ageDays / 30.0);
                        }).Take(30);

                        foreach (var act in orderedActions)
                        {
                            trimmedActions[act.Key] = act.Value;
                        }
                        actions = trimmedActions;
                    }

                    if (actions.Count > 0)
                    {
                        selectionUsage[kvp.Key] = actions;
                    }
                }

                if (selectionUsage.Count > 200)
                {
                    var trimmedUsage = new Dictionary<string, Dictionary<string, SelectionUsageEntry>>(StringComparer.OrdinalIgnoreCase);
                    var orderedUsage = selectionUsage.OrderByDescending(x =>
                    {
                        DateTimeOffset mostRecent = DateTimeOffset.MinValue;
                        foreach (var entry in x.Value.Values)
                        {
                            if (entry.LastUsedUtc > mostRecent)
                            {
                                mostRecent = entry.LastUsedUtc;
                            }
                        }
                        return mostRecent;
                    }).Take(200);

                    foreach (var usg in orderedUsage)
                    {
                        trimmedUsage[usg.Key] = usg.Value;
                    }
                    selectionUsage = trimmedUsage;
                }
            }

            return new UserSettings(
                UserSettings.CurrentVersion,
                new HashSet<string>(pinnedActions, StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, int>(recentActions, StringComparer.OrdinalIgnoreCase),
                normalizedActionPreferences,
                settings.GlobalShortcut,
                settings.ImageExportPresets,
                settings.HasSeenFirstRun,
                settings.AfterFileAction,
                settings.ActionHistory,
                selectionUsage,
                PowerTrove.Application.Localization.UiLanguages.Normalize(settings.UiLanguage),
                settings.ShowNotInstalledActions,
                settings.RunFileToolsInSeparateProcess,
                settings.CheckForUpdatesOnStartup,
                settings.PackMigrationChecked,
                settings.PendingPackMigration?.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToArray(),
                settings.CheckForPackUpdatesAutomatically
            );
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error loading settings, recovering with defaults: {ex.Message}");
            BackupSettings(".corrupt");
            needsSave = true;
            return UserSettings.CreateDefault();
        }
    }

    private void BackupSettings(string suffix)
    {
        try
        {
            if (File.Exists(_settingsFilePath))
            {
                string backupPath = $"{_settingsFilePath}{suffix}.{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
                File.Copy(_settingsFilePath, backupPath, overwrite: true);
            }
        }
        catch
        {
            // Best effort
        }
    }

    public async Task SaveAsync()
    {
        if (IsReadOnly)
        {
            return;
        }

        UserSettings settingsToSave;
        lock (_lock)
        {
            settingsToSave = _settings;
        }

        try
        {
            string directory = Path.GetDirectoryName(_settingsFilePath)!;
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(settingsToSave, options);

            // Safe write
            string tempPath = _settingsFilePath + ".tmp";
            await File.WriteAllTextAsync(tempPath, json).ConfigureAwait(false);
            File.Move(tempPath, _settingsFilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error saving settings: {ex.Message}");
        }
    }

    public void Save()
    {
        SaveAsync().GetAwaiter().GetResult();
    }

    public void PinAction(string actionId)
    {
        lock (_lock)
        {
            var pinned = new HashSet<string>(_settings.PinnedActions, StringComparer.OrdinalIgnoreCase);
            if (pinned.Add(actionId))
            {
                _settings = _settings with { PinnedActions = pinned };
                // Synchronous save for simplicity right now, can be optimized later
                Save();
            }
        }
    }

    public void UnpinAction(string actionId)
    {
        lock (_lock)
        {
            var pinned = new HashSet<string>(_settings.PinnedActions, StringComparer.OrdinalIgnoreCase);
            if (pinned.Remove(actionId))
            {
                _settings = _settings with { PinnedActions = pinned };
                Save();
            }
        }
    }

    public void RecordActionExecution(string actionId)
    {
        lock (_lock)
        {
            var recent = new Dictionary<string, int>(_settings.RecentActions, StringComparer.OrdinalIgnoreCase);
            if (recent.TryGetValue(actionId, out int currentScore))
            {
                recent[actionId] = currentScore + 1;
            }
            else
            {
                recent[actionId] = 1;
            }

            _settings = _settings with { RecentActions = recent };
            Save();
        }
    }

    public void SetActionPreference(ActionDescriptor descriptor, string key, string value)
    {
        if (descriptor.Parameters != null)
        {
            foreach (var parameter in descriptor.Parameters)
            {
                if (string.Equals(parameter.Name, key, StringComparison.OrdinalIgnoreCase) &&
                    parameter.Kind == ActionParameterKind.Password)
                {
                    return;
                }
            }
        }
        SetActionPreference(descriptor.Id, key, value);
    }

    public void SetActionPreference(string actionId, string key, string value)
    {
        lock (_lock)
        {
            var prefs = new Dictionary<string, Dictionary<string, string>>(_settings.ActionPreferences, StringComparer.OrdinalIgnoreCase);

            Dictionary<string, string> actionPrefs;
            if (prefs.TryGetValue(actionId, out var existing))
            {
                actionPrefs = new Dictionary<string, string>(existing, StringComparer.OrdinalIgnoreCase);
            }
            else
            {
                actionPrefs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            actionPrefs[key] = value;
            prefs[actionId] = actionPrefs;

            _settings = _settings with { ActionPreferences = prefs };
            Save();
        }
    }

    public void SetUiLanguage(string? language)
    {
        lock (_lock)
        {
            _settings = _settings with { UiLanguage = PowerTrove.Application.Localization.UiLanguages.Normalize(language) };
            Save();
        }
    }

    public void SetCheckForPackUpdatesAutomatically(bool enabled)
    {
        lock (_lock)
        {
            if (IsReadOnly) return;
            _settings = _settings with { CheckForPackUpdatesAutomatically = enabled };
            Save();
        }
    }

    public void SetShowNotInstalledActions(bool enabled)
    {
        lock (_lock)
        {
            if (IsReadOnly) return;
            _settings = _settings with { ShowNotInstalledActions = enabled };
            Save();
        }
    }

    public void SetCheckForUpdatesOnStartup(bool enabled)
    {
        lock (_lock)
        {
            if (IsReadOnly) return;
            _settings = _settings with { CheckForUpdatesOnStartup = enabled };
            Save();
        }
    }

    public bool HadExistingSettings { get; }
    public bool PackMigrationChecked => Settings.PackMigrationChecked;
    public IReadOnlyList<string> PendingPackMigration => Settings.PendingPackMigration ?? [];
    public void SetPackMigration(IReadOnlyList<string> pending)
    {
        ArgumentNullException.ThrowIfNull(pending);
        lock (_lock)
        {
            if (IsReadOnly) return;
            _settings = _settings with { PackMigrationChecked = true, PendingPackMigration = pending.Distinct(StringComparer.Ordinal).ToArray() };
            Save();
        }
    }

    public void SetRunFileToolsInSeparateProcess(bool enabled)
    {
        lock (_lock)
        {
            if (IsReadOnly) return;
            _settings = _settings with { RunFileToolsInSeparateProcess = enabled };
            Save();
        }
    }

    public void SetGlobalShortcut(PowerTrove.Core.Input.HotkeyConfiguration shortcut)
    {
        lock (_lock)
        {
            _settings = _settings with { GlobalShortcut = shortcut };
            Save();
        }
    }


    public void AddImageExportPreset(SavedImageExportPreset preset)
    {
        lock (_lock)
        {
            var presets = _settings.ImageExportPresets != null
                ? new List<SavedImageExportPreset>(_settings.ImageExportPresets)
                : new List<SavedImageExportPreset>();

            // Update if exists, otherwise add
            var existingIndex = presets.FindIndex(p => string.Equals(p.Id, preset.Id, StringComparison.OrdinalIgnoreCase));
            if (existingIndex >= 0)
            {
                presets[existingIndex] = preset;
            }
            else
            {
                presets.Add(preset);
            }

            _settings = _settings with { ImageExportPresets = presets };
            Save();
        }
    }

    public void RemoveImageExportPreset(string id)
    {
        lock (_lock)
        {
            if (_settings.ImageExportPresets != null)
            {
                var presets = new List<SavedImageExportPreset>(_settings.ImageExportPresets);
                var removed = presets.RemoveAll(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
                if (removed > 0)
                {
                    _settings = _settings with { ImageExportPresets = presets };
                    Save();
                }
            }
        }
    }

    public void ClearRecentActions()
    {
        lock (_lock)
        {
            _settings = _settings with { RecentActions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) };
            Save();
        }
    }

    public void ResetToDefaults()
    {
        lock (_lock)
        {
            // Keep pinned actions as they are user's explicit setup, but we could clear them if required
            // Let's clear everything to really reset to default.
            _settings = UserSettings.CreateDefault();
            Save();
        }
    }

    public void AddActionHistory(ActionHistoryItem item)
    {
        lock (_lock)
        {
            var history = _settings.ActionHistory != null
                ? new System.Collections.Generic.List<ActionHistoryItem>(_settings.ActionHistory)
                : new System.Collections.Generic.List<ActionHistoryItem>();

            history.Insert(0, item);

            // Keep only the last 50 items
            if (history.Count > 50)
            {
                history.RemoveRange(50, history.Count - 50);
            }

            _settings = _settings with { ActionHistory = history };
            Save();
        }
    }

    public void MarkFirstRunSeen()
    {
        lock (_lock)
        {
            _settings = _settings with { HasSeenFirstRun = true };
            Save();
        }
    }

    public void ClearSelectionUsage()
    {
        lock (_lock)
        {
            _settings = _settings with { SelectionUsage = new Dictionary<string, Dictionary<string, SelectionUsageEntry>>(StringComparer.OrdinalIgnoreCase) };
            Save();
        }
    }

    public void RecordSelectionUsage(string signatureKey, string actionId, DateTimeOffset now)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(signatureKey, @"^(files:[a-z+]+:(one|few|several|many)|table|text|none)$"))
        {
            return;
        }

        lock (_lock)
        {
            var selectionUsage = _settings.SelectionUsage != null
                ? new Dictionary<string, Dictionary<string, SelectionUsageEntry>>(_settings.SelectionUsage, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, Dictionary<string, SelectionUsageEntry>>(StringComparer.OrdinalIgnoreCase);

            if (!selectionUsage.TryGetValue(signatureKey, out var actions))
            {
                actions = new Dictionary<string, SelectionUsageEntry>(StringComparer.OrdinalIgnoreCase);
            }
            else
            {
                actions = new Dictionary<string, SelectionUsageEntry>(actions, StringComparer.OrdinalIgnoreCase);
            }

            if (actions.TryGetValue(actionId, out var existing))
            {
                actions[actionId] = new SelectionUsageEntry(existing.Count + 1, now);
            }
            else
            {
                actions[actionId] = new SelectionUsageEntry(1, now);
            }

            if (actions.Count > 30)
            {
                string lowestScoreId = null;
                double lowestScore = double.MaxValue;

                foreach (var kvp in actions)
                {
                    double ageDays = (now - kvp.Value.LastUsedUtc).TotalDays;
                    double score = kvp.Value.Count * Math.Pow(0.5, ageDays / 30.0);
                    if (score < lowestScore)
                    {
                        lowestScore = score;
                        lowestScoreId = kvp.Key;
                    }
                }
                if (lowestScoreId != null)
                {
                    actions.Remove(lowestScoreId);
                }
            }

            selectionUsage[signatureKey] = actions;

            if (selectionUsage.Count > 200)
            {
                string oldestKey = null;
                DateTimeOffset oldestTime = DateTimeOffset.MaxValue;
                foreach(var kvp in selectionUsage)
                {
                    DateTimeOffset mostRecentForSignature = DateTimeOffset.MinValue;
                    foreach(var actionEntry in kvp.Value.Values)
                    {
                        if (actionEntry.LastUsedUtc > mostRecentForSignature)
                        {
                            mostRecentForSignature = actionEntry.LastUsedUtc;
                        }
                    }
                    if (mostRecentForSignature < oldestTime)
                    {
                        oldestTime = mostRecentForSignature;
                        oldestKey = kvp.Key;
                    }
                }
                if (oldestKey != null)
                {
                    selectionUsage.Remove(oldestKey);
                }
            }

            _settings = _settings with { SelectionUsage = selectionUsage };
            Save();
        }
    }
}
