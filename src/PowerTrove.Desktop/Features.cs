namespace PowerTrove.Desktop;

/// <summary>
/// Features in development that release builds leave out. The switches are set in PowerTrove.Desktop.csproj:
/// on in Debug builds, off in Release builds unless enabled explicitly.
/// </summary>
public static class Features
{
    /// <summary>
    /// User-defined recipes: the Create Recipe command, the recipe editor, the Settings section and saved recipes
    /// in the palette. Off means none of them is shown; recipes already saved stay in the settings.
    /// Enable in a Release build with -p:PowerTroveUserRecipes=true.
    /// </summary>
#if POWERTROVE_USER_RECIPES
    public static bool UserRecipes { get; } = true;
#else
    public static bool UserRecipes { get; } = false;
#endif
}
