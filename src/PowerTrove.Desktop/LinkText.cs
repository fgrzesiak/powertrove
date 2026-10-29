using System;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;

namespace PowerTrove.Desktop;

/// <summary>
/// Shows text that may contain links written as [label](uri), as used in action preview notes. Only https and
/// Microsoft Store links are made clickable; anything else stays plain text.
/// </summary>
internal static partial class LinkText
{
    [GeneratedRegex(@"\[([^\]]+)\]\(((?:https|ms-windows-store)://[^\s)]+)\)")]
    private static partial Regex LinkPattern();

    public static void Set(TextBlock target, string text)
    {
        target.Inlines.Clear();
        int last = 0;
        foreach (Match match in LinkPattern().Matches(text))
        {
            if (match.Index > last) target.Inlines.Add(new Run { Text = text[last..match.Index] });
            var link = new Hyperlink();
            link.Inlines.Add(new Run { Text = match.Groups[1].Value });
            var uri = new Uri(match.Groups[2].Value);
            link.Click += async (_, _) => await Windows.System.Launcher.LaunchUriAsync(uri);
            target.Inlines.Add(link);
            last = match.Index + match.Length;
        }
        if (last < text.Length) target.Inlines.Add(new Run { Text = text[last..] });
    }
}
