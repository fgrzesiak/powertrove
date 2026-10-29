using System.Threading.Tasks;
using PowerTrove.Abstractions;
using PowerTrove.Core.Input;

namespace PowerTrove.Application.Configuration;

public interface IPackVisibilitySettings
{
    void SetShowNotInstalledActions(bool enabled);
}

public interface IUpdateCheckSettings
{
    void SetCheckForUpdatesOnStartup(bool enabled);
}

public interface IPackUpdateCheckSettings
{
    void SetCheckForPackUpdatesAutomatically(bool enabled);
}

/// <summary>Takes over the packs an earlier version compiled in, once, after updating to a Core-only version.</summary>
public interface IPackMigrationSettings
{
    /// <summary>True when settings existed before this start, so this is not a fresh installation.</summary>
    bool HadExistingSettings { get; }
    bool PackMigrationChecked { get; }
    IReadOnlyList<string> PendingPackMigration { get; }
    /// <summary>Records the packs still to take over and marks the migration as checked.</summary>
    void SetPackMigration(IReadOnlyList<string> pending);
}

public interface ISettingsService
{
    UserSettings Settings { get; }

    bool IsReadOnly { get; }

    Task SaveAsync();

    void PinAction(string actionId);

    void UnpinAction(string actionId);

    void RecordActionExecution(string actionId);

    void SetActionPreference(string actionId, string key, string value);

    void SetActionPreference(ActionDescriptor descriptor, string key, string value);

    void SetGlobalShortcut(HotkeyConfiguration shortcut);

    void ClearRecentActions();

    void ResetToDefaults();

    void AddImageExportPreset(SavedImageExportPreset preset);

    void RemoveImageExportPreset(string id);

    void AddActionHistory(ActionHistoryItem item);

    void MarkFirstRunSeen();
    void RecordSelectionUsage(string signatureKey, string actionId, System.DateTimeOffset now);

    void ClearSelectionUsage();

    /// <summary>Stores the display language ("en", "de") or null to follow Windows; applies after a restart.</summary>
    void SetUiLanguage(string? language);

    /// <summary>Stores the file-tool worker preference; applies after a restart.</summary>
    void SetRunFileToolsInSeparateProcess(bool enabled);
}
