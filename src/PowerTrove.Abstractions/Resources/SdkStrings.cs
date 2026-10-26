using System.Globalization;
using System.Resources;

namespace PowerTrove.Abstractions.Resources;

/// <summary>User-visible texts of the SDK's shared file-action pipeline (ADR 0009).</summary>
public static class SdkStrings
{
    private static readonly ResourceManager Resources =
        new("PowerTrove.Abstractions.Resources.Strings", typeof(SdkStrings).Assembly);

    public static string AppliesToSome(int accepted, int total) =>
        string.Format(CultureInfo.CurrentCulture, Get("AppliesToSome"), accepted, total);

    public static string Action_ChangesFiles => Get("Action_ChangesFiles");
    public static string Error_FileNoLongerExists => Get("Error_FileNoLongerExists");
    public static string Error_FolderNoLongerExists => Get("Error_FolderNoLongerExists");
    public static string Error_ContentNotSupported(string displayName) => string.Format(CultureInfo.CurrentCulture, Get("Error_ContentNotSupported"), displayName);
    public static string Error_AccessDenied => Get("Error_AccessDenied");
    public static string Error_FileNotFound => Get("Error_FileNotFound");
    public static string Error_FolderNotFound => Get("Error_FolderNotFound");
    public static string Error_PathTooLong => Get("Error_PathTooLong");
    public static string Error_FileSystemError => Get("Error_FileSystemError");
    public static string Error_OperationNotSupported => Get("Error_OperationNotSupported");
    public static string Error_ProcessingFailed => Get("Error_ProcessingFailed");

    public static string ContextUnavailable => Get("ContextUnavailable");
    public static string MissingRequiredCapabilities => Get("MissingRequiredCapabilities");
    public static string RequiresType(string typeList, string currentKind) => string.Format(System.Globalization.CultureInfo.CurrentCulture, Get("RequiresType"), typeList, currentKind);
    public static string RequiresMinItems(int count, string familyName) => string.Format(System.Globalization.CultureInfo.CurrentCulture, Get(count == 1 ? "RequiresMinItems_One" : "RequiresMinItems_Other"), count, familyName);
    public static string RequiresMaxItems(int count, string familyName) => string.Format(System.Globalization.CultureInfo.CurrentCulture, Get(count == 1 ? "RequiresMaxItems_One" : "RequiresMaxItems_Other"), count, familyName);
    public static string RemoveOtherItems => Get("RemoveOtherItems");
    public static string Error_SelectionExpired => Get("Error_SelectionExpired");
    public static string Error_NotAFileSelection => Get("Error_NotAFileSelection");
    public static string Error_NoSupportedFiles(string displayName) => string.Format(CultureInfo.CurrentCulture, Get("Error_NoSupportedFiles"), displayName);
    public static string Error_SelectAtLeast(int count, string displayName) => string.Format(CultureInfo.CurrentCulture, Get("Error_SelectAtLeast"), count, displayName);
    public static string Error_SelectAtMost(int count, string displayName) => string.Format(CultureInfo.CurrentCulture, Get("Error_SelectAtMost"), count, displayName);
    public static string Error_ChooseExistingFileTypes(string types) => string.Format(CultureInfo.CurrentCulture, Get("Error_ChooseExistingFileTypes"), types);
    public static string Error_ChooseExistingFile => Get("Error_ChooseExistingFile");

    public static string Summary_Canceled => Get("Summary_Canceled");

    public static string Summary_CanceledOutputs(int count) => count == 1
        ? Get("Summary_CanceledOutputs_One")
        : string.Format(CultureInfo.CurrentCulture, Get("Summary_CanceledOutputs_Other"), count);

    public static string Summary_Created(int outputs) => outputs == 1
        ? Get("Summary_Created_One")
        : string.Format(CultureInfo.CurrentCulture, Get("Summary_Created_Other"), outputs);

    public static string Summary_CreatedFailed(int outputs, int failed) => (outputs == 1, failed == 1) switch
    {
        (true, true) => Get("Summary_CreatedFailed_One_One"),
        (true, false) => string.Format(CultureInfo.CurrentCulture, Get("Summary_CreatedFailed_One_Other"), outputs, failed),
        (false, true) => string.Format(CultureInfo.CurrentCulture, Get("Summary_CreatedFailed_Other_One"), outputs, failed),
        (false, false) => string.Format(CultureInfo.CurrentCulture, Get("Summary_CreatedFailed_Other_Other"), outputs, failed)
    };

    public static string Summary_NoOutput => Get("Summary_NoOutput");

    public static string Summary_Skipped(int count) => count == 1
        ? Get("Summary_Skipped_One")
        : string.Format(CultureInfo.CurrentCulture, Get("Summary_Skipped_Other"), count);

    public static string Summary_NeededNoChanges(int count) => count == 1
        ? Get("Summary_NeededNoChanges_One")
        : string.Format(CultureInfo.CurrentCulture, Get("Summary_NeededNoChanges_Other"), count);

    public static string Summary_Saved(int outputs, string diff, string input, string produced) => outputs == 1
        ? string.Format(CultureInfo.CurrentCulture, Get("Summary_Saved_One"), outputs, diff, input, produced)
        : string.Format(CultureInfo.CurrentCulture, Get("Summary_Saved_Other"), outputs, diff, input, produced);

    public static string Summary_Larger(int outputs, string diff, string input, string produced) => outputs == 1
        ? string.Format(CultureInfo.CurrentCulture, Get("Summary_Larger_One"), outputs, diff, input, produced)
        : string.Format(CultureInfo.CurrentCulture, Get("Summary_Larger_Other"), outputs, diff, input, produced);

    public static string Progress_Starting => Get("Progress_Starting");
    public static string Progress_Processing => Get("Progress_Processing");
    public static string Progress_Item(string verb, int i, int count) => string.Format(CultureInfo.CurrentCulture, Get("Progress_Item"), verb, i, count);
    public static string Progress_Done => Get("Progress_Done");

    public static string Error_AtLeastValidRequired(int min, string displayName) => string.Format(CultureInfo.CurrentCulture, Get("Error_AtLeastValidRequired"), min, displayName);
    public static string Progress_Working => Get("Progress_Working");

    public static string Unit_B => Get("Unit_B");
    public static string Unit_KB => Get("Unit_KB");
    public static string Unit_MB => Get("Unit_MB");
    public static string Unit_GB => Get("Unit_GB");
    public static string Unit_TB => Get("Unit_TB");

    public static string Error_EngineNoOutput => Get("Error_EngineNoOutput");
    public static string Error_SaveNoFreeName => Get("Error_SaveNoFreeName");
    public static string Error_OutputFolderUnavailable => Get("Error_OutputFolderUnavailable");
    public static string Error_OutputFolderNoLongerExists => Get("Error_OutputFolderNoLongerExists");
    public static string Error_ItemNeededNoChanges => Get("Error_ItemNeededNoChanges");

    private static string Get(string key) => Resources.GetString(key, CultureInfo.CurrentUICulture) ?? key;
}
