using System;
using System.Resources;

namespace PowerTrove.Core.Resources;

public static class Strings
{
    private static readonly ResourceManager ResourceManager = new ResourceManager("PowerTrove.Core.Resources.Strings", typeof(Strings).Assembly);



    public static string ActionNotAvailable => GetString("ActionNotAvailable");
    public static string ActionAvailabilityUnknown => GetString("ActionAvailabilityUnknown");
    public static string CatalogDownload(string pack, double megabytes) => string.Format(System.Globalization.CultureInfo.CurrentCulture, GetString("CatalogDownload"), pack, megabytes);
    public static string CatalogDownloadUnknown(string pack) => string.Format(System.Globalization.CultureInfo.CurrentCulture, GetString("CatalogDownloadUnknown"), pack);
    public static string CatalogAccessibleName(string title, string reason) => string.Format(System.Globalization.CultureInfo.CurrentCulture, GetString("CatalogAccessibleName"), title, reason);
    public static string CatalogSelectionUnsupported => GetString("CatalogSelectionUnsupported");
    public static string CatalogLoadFailed => GetString("CatalogLoadFailed");
    public static string WorkflowNoSteps => GetString("WorkflowNoSteps");
    public static string WorkflowFailed(string title) => string.Format(System.Globalization.CultureInfo.CurrentCulture, GetString("WorkflowFailed"), title);
    public static string NoStepsToExecute => GetString("NoStepsToExecute");
    public static string WorkflowCompleted(string title) => string.Format(System.Globalization.CultureInfo.CurrentCulture, GetString("WorkflowCompleted"), title);
    public static string WorkflowStepStarting(string actionId) => string.Format(System.Globalization.CultureInfo.CurrentCulture, GetString("WorkflowStepStarting"), actionId);
    public static string WorkflowActionNotFound(string actionId) => string.Format(System.Globalization.CultureInfo.CurrentCulture, GetString("WorkflowActionNotFound"), actionId);
    public static string WorkflowComplete => GetString("WorkflowComplete");
    public static string WorkflowPreviewTitle(string title) => string.Format(System.Globalization.CultureInfo.CurrentCulture, GetString("WorkflowPreviewTitle"), title);

    public static string RecipeNoSteps => GetString("RecipeNoSteps");
    public static string ContextExpired => GetString("ContextExpired");

    private static string GetString(string name)
    {
        return ResourceManager.GetString(name, System.Globalization.CultureInfo.CurrentUICulture) ?? name;
    }
}
