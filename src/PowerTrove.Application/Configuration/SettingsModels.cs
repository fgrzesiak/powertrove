using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace PowerTrove.Application.Configuration;



public sealed record ActionHistoryItem(
    string ActionId,
    DateTimeOffset ExecutedAt,
    bool IsSuccess,
    int OutputCount,
    int FailureCount
);

public sealed record SavedImageExportPreset(

    string Id,
    string Title,
    string Description,
    string Format,
    int Quality,
    int? MaxWidth,
    int? MaxHeight,
    bool NoUpscale,
    bool StripMetadata
);

public sealed record SelectionUsageEntry(int Count, DateTimeOffset LastUsedUtc);

public sealed record UserSettings(
    [property: JsonRequired] int Version,
    HashSet<string> PinnedActions,
    Dictionary<string, int> RecentActions,
    Dictionary<string, Dictionary<string, string>> ActionPreferences,
    PowerTrove.Core.Input.HotkeyConfiguration? GlobalShortcut = null,
    IReadOnlyList<SavedImageExportPreset>? ImageExportPresets = null,
    bool HasSeenFirstRun = false,
    AfterFileAction AfterFileAction = AfterFileAction.KeepResultVisible,
    IReadOnlyList<ActionHistoryItem>? ActionHistory = null,
    Dictionary<string, Dictionary<string, SelectionUsageEntry>>? SelectionUsage = null,
    string? UiLanguage = null,
    bool ShowNotInstalledActions = true,
    bool RunFileToolsInSeparateProcess = false,
    bool CheckForUpdatesOnStartup = true,
    bool PackMigrationChecked = false,
    IReadOnlyList<string>? PendingPackMigration = null,
    bool CheckForPackUpdatesAutomatically = true)
{
    public const int CurrentVersion = 3;

    public static UserSettings CreateDefault()
    {
        return new UserSettings(
            Version: CurrentVersion,
            PinnedActions: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            RecentActions: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
            ActionPreferences: new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase),
            GlobalShortcut: null,
            ImageExportPresets: null,
            HasSeenFirstRun: false,
            AfterFileAction: AfterFileAction.KeepResultVisible,
            ActionHistory: null,
            SelectionUsage: null
        );
    }
}
