using System;
using System.Globalization;

namespace PowerTrove.Abstractions.Localization;

/// <summary>CLDR cardinal categories for the ten supported UI languages.</summary>
public static class PluralForms
{
    public static string Category(int count, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        string language = culture.TwoLetterISOLanguageName;
        if (language == "pl")
        {
            if (count == 1) return "One";
            int mod10 = count % 10, mod100 = count % 100;
            if (mod10 is >= 2 and <= 4 && mod100 is not (>= 12 and <= 14)) return "Few";
            if (count != 1 && (mod10 is >= 0 and <= 1 || mod10 is >= 5 and <= 9 ||
                mod100 is >= 12 and <= 14)) return "Many";
            return "Other";
        }
        if (language is "ja" or "tr" or "id") return "Other";
        // CLDR puts 0 into "one" for French and Portuguese, but the shipped One texts are written
        // for exactly one item (some spell out "1"), and "0 fichiers" reads naturally.
        return count == 1 ? "One" : "Other";
    }

    /// <summary>
    /// The text of a <c>stem + separator + One/Other</c> family for a count. A language may add
    /// <c>Few</c> and <c>Many</c> members (Polish); a missing member falls back to <c>Other</c>.
    /// </summary>
    public static string Text(string stem, int count, Func<string, string?> lookup, string separator = "_")
    {
        ArgumentNullException.ThrowIfNull(lookup);
        string category = Category(count, CultureInfo.CurrentUICulture);
        return lookup(stem + separator + category) ?? lookup(stem + separator + "Other") ?? stem + separator + "Other";
    }

    /// <summary>
    /// Chooses between a singular and a plural resource for a count. A language may add
    /// <c>pluralKey + "Few"</c> and <c>pluralKey + "Many"</c> for its other forms (Polish).
    /// </summary>
    public static string Choose(int count, string singularKey, string pluralKey, Func<string, string?> lookup)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        string category = Category(count, CultureInfo.CurrentUICulture);
        if (category == "One") return lookup(singularKey) ?? singularKey;
        return lookup(pluralKey + category) ?? lookup(pluralKey) ?? pluralKey;
    }

    /// <summary>"{count} {noun}" from a singular and a plural resource, see <see cref="Choose"/>.</summary>
    public static string Noun(int count, string singularKey, string pluralKey, Func<string, string?> lookup) =>
        count.ToString(CultureInfo.CurrentCulture) + " " + Choose(count, singularKey, pluralKey, lookup);

    public static string ResourceKey(string stem, int count, CultureInfo culture, Func<string, bool> exists, string separator = "")
    {
        ArgumentNullException.ThrowIfNull(exists);
        string preferred = stem + separator + Category(count, culture);
        return exists(preferred) ? preferred : stem + separator + "Other";
    }
}
