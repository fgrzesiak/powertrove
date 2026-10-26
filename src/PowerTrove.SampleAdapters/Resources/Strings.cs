using System;
using System.Resources;

namespace PowerTrove.SampleAdapters.Resources;

public static class Strings
{
    private static readonly ResourceManager ResourceManager = new ResourceManager("PowerTrove.SampleAdapters.Resources.Strings", typeof(Strings).Assembly);

    public static string Action_CountWords_Title => GetString("Action_CountWords_Title");
    public static string Action_CountWords_Description => GetString("Action_CountWords_Description");
    public static string RequiresNonEmptyText => GetString("RequiresNonEmptyText");
    public static string NoTextToCount => GetString("NoTextToCount");
    public static string WordsCounted(int count) => string.Format(System.Globalization.CultureInfo.CurrentCulture, GetString(count == 1 ? "WordsCounted_One" : "WordsCounted_Other"), count);
    public static string ContextStale => GetString("ContextStale");
    public static string InvalidContext => GetString("InvalidContext");
    public static string CountCompleted => GetString("CountCompleted");
    public static string SuccessfullyCountedWords(int count) => string.Format(System.Globalization.CultureInfo.CurrentCulture, GetString(count == 1 ? "SuccessfullyCountedWords_One" : "SuccessfullyCountedWords_Other"), count);

    public static string Action_ReverseText_Title => GetString("Action_ReverseText_Title");
    public static string Action_ReverseText_Description => GetString("Action_ReverseText_Description");
    public static string NoTextToReverse => GetString("NoTextToReverse");
    public static string ReversePreview(string text) => string.Format(System.Globalization.CultureInfo.CurrentCulture, GetString("ReversePreview"), text);
    public static string StartingTextReversal => GetString("StartingTextReversal");
    public static string TextReversedSuccessfully => GetString("TextReversedSuccessfully");
    public static string ReverseSuccessMessage => GetString("ReverseSuccessMessage");

    private static string GetString(string name)
    {
        return ResourceManager.GetString(name, System.Globalization.CultureInfo.CurrentUICulture) ?? name;
    }
}
