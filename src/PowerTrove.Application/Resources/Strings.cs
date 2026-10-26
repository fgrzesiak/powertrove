using System;
using System.Resources;

namespace PowerTrove.Application.Resources;

public static class Strings
{
    private static readonly ResourceManager ResourceManager = new ResourceManager("PowerTrove.Application.Resources.Strings", typeof(Strings).Assembly);

    public static string Summary_ImagesSingular => GetString("Summary_ImagesSingular");
    public static string Summary_ImagesPlural => GetString("Summary_ImagesPlural");
    public static string Summary_FoldersSingular => GetString("Summary_FoldersSingular");
    public static string Summary_FoldersPlural => GetString("Summary_FoldersPlural");
    public static string Summary_FilesSingular => GetString("Summary_FilesSingular");
    public static string Summary_FilesPlural => GetString("Summary_FilesPlural");
    public static string Summary_OfflineStatus => GetString("Summary_OfflineStatus");
    public static string Summary_TableDimensions => GetString("Summary_TableDimensions");
    public static string Search_ErrorDefault => GetString("Search_ErrorDefault");
    public static string Context_None => GetString("Context_None");
    public static string Context_Unavailable => GetString("Context_Unavailable");
    public static string Notice_Unsupported => GetString("Notice_Unsupported");
    public static string Notice_Denied => GetString("Notice_Denied");
    public static string Notice_TimedOut => GetString("Notice_TimedOut");
    public static string Notice_Failed => GetString("Notice_Failed");
    public static string Action_OpenSettings_Title => GetString("Action_OpenSettings_Title");
    public static string Action_OpenSettings_Description => GetString("Action_OpenSettings_Description");
    public static string Group_Suggested => GetString("Group_Suggested");
    public static string Group_All => GetString("Group_All");
    public static string Badge_Favorite => GetString("Badge_Favorite");
    public static string Badge_Recommended => GetString("Badge_Recommended");

    public static string GetString(string name)
    {
        return ResourceManager.GetString(name, System.Globalization.CultureInfo.CurrentUICulture) ?? name;
    }
}
