using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using ContextBar.Abstractions.Resources;
using ContextBar.Abstractions.Files;
using Xunit;

namespace ContextBar.Abstractions.Tests.Localization;

public class FileActionMessagesTests
{
    [Fact]
    public void AppliesToSome_UsesCorrectEnglishString()
    {
        Assert.Equal("Applies to 1 of 2 items.", SdkStrings.AppliesToSome(1, 2));
    }

    [Fact]
    public void AppliesToSome_UsesCorrectGermanString()
    {
        var prevUi = System.Globalization.CultureInfo.CurrentUICulture;
        var prev = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo("de-DE");
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal("Gilt f\u00FCr 1 von 2 Elementen.", SdkStrings.AppliesToSome(1, 2));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentUICulture = prevUi;
            System.Globalization.CultureInfo.CurrentCulture = prev;
        }
    }

    [Theory]
    [InlineData(0, "Created 0 outputs.")]
    [InlineData(1, "Created 1 output.")]
    [InlineData(2, "Created 2 outputs.")]
    public void Created_UsesCorrectPluralInEnglish(int count, string expected)
    {
        Assert.Equal(expected, SdkStrings.Summary_Created(count));
    }

    [Theory]
    [InlineData(0, "0 Ausgaben erstellt.")]
    [InlineData(1, "1 Ausgabe erstellt.")]
    [InlineData(2, "2 Ausgaben erstellt.")]
    public void Created_UsesCorrectPluralInGerman(int count, string expected)
    {
        var prevUi = CultureInfo.CurrentUICulture;
        var prev = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("de-DE");
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal(expected, SdkStrings.Summary_Created(count));
        }
        finally
        {
            CultureInfo.CurrentUICulture = prevUi;
            CultureInfo.CurrentCulture = prev;
        }
    }

    [Theory]
    [InlineData(0, 0, "Created 0 outputs; 0 item(s) failed.")]
    [InlineData(1, 0, "Created 1 output; 0 item(s) failed.")]
    [InlineData(0, 1, "Created 0 outputs; 1 item(s) failed.")]
    [InlineData(1, 1, "Created 1 output; 1 item(s) failed.")]
    [InlineData(2, 1, "Created 2 outputs; 1 item(s) failed.")]
    [InlineData(1, 2, "Created 1 output; 2 item(s) failed.")]
    [InlineData(2, 2, "Created 2 outputs; 2 item(s) failed.")]
    public void CreatedFailed_UsesCorrectPluralsInEnglish(int outputs, int failed, string expected)
    {
        Assert.Equal(expected, SdkStrings.Summary_CreatedFailed(outputs, failed));
    }

    [Theory]
    [InlineData(0, 0, "0 Ausgaben erstellt; 0 Elemente sind fehlgeschlagen.")]
    [InlineData(1, 0, "1 Ausgabe erstellt; 0 Elemente sind fehlgeschlagen.")]
    [InlineData(0, 1, "0 Ausgaben erstellt; 1 Element ist fehlgeschlagen.")]
    [InlineData(1, 1, "1 Ausgabe erstellt; 1 Element ist fehlgeschlagen.")]
    [InlineData(2, 1, "2 Ausgaben erstellt; 1 Element ist fehlgeschlagen.")]
    [InlineData(1, 2, "1 Ausgabe erstellt; 2 Elemente sind fehlgeschlagen.")]
    [InlineData(2, 2, "2 Ausgaben erstellt; 2 Elemente sind fehlgeschlagen.")]
    public void CreatedFailed_UsesCorrectPluralsInGerman(int outputs, int failed, string expected)
    {
        var prevUi = CultureInfo.CurrentUICulture;
        var prev = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("de-DE");
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal(expected, SdkStrings.Summary_CreatedFailed(outputs, failed));
        }
        finally
        {
            CultureInfo.CurrentUICulture = prevUi;
            CultureInfo.CurrentCulture = prev;
        }
    }

    [Theory]
    [InlineData(0, "Skipped 0 unsupported item(s).")]
    [InlineData(1, "Skipped 1 unsupported item(s).")]
    [InlineData(2, "Skipped 2 unsupported item(s).")]
    public void Skipped_UsesCorrectPluralInEnglish(int count, string expected)
    {
        Assert.Equal(expected, SdkStrings.Summary_Skipped(count));
    }

    [Theory]
    [InlineData(0, "0 nicht unterstützte Elemente übersprungen.")]
    [InlineData(1, "1 nicht unterstütztes Element übersprungen.")]
    [InlineData(2, "2 nicht unterstützte Elemente übersprungen.")]
    public void Skipped_UsesCorrectPluralInGerman(int count, string expected)
    {
        var prevUi = CultureInfo.CurrentUICulture;
        var prev = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("de-DE");
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal(expected, SdkStrings.Summary_Skipped(count));
        }
        finally
        {
            CultureInfo.CurrentUICulture = prevUi;
            CultureInfo.CurrentCulture = prev;
        }
    }

    [Theory]
    [InlineData(0, "Canceled. 0 outputs were completed before cancellation.")]
    [InlineData(1, "Canceled. 1 output was completed before cancellation.")]
    [InlineData(2, "Canceled. 2 outputs were completed before cancellation.")]
    public void CanceledOutputs_UsesCorrectPluralInEnglish(int count, string expected)
    {
        Assert.Equal(expected, SdkStrings.Summary_CanceledOutputs(count));
    }

    [Theory]
    [InlineData(0, "Abgebrochen. 0 Ausgaben wurden vor dem Abbruch abgeschlossen.")]
    [InlineData(1, "Abgebrochen. 1 Ausgabe wurde vor dem Abbruch abgeschlossen.")]
    [InlineData(2, "Abgebrochen. 2 Ausgaben wurden vor dem Abbruch abgeschlossen.")]
    public void CanceledOutputs_UsesCorrectPluralInGerman(int count, string expected)
    {
        var prevUi = CultureInfo.CurrentUICulture;
        var prev = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("de-DE");
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal(expected, SdkStrings.Summary_CanceledOutputs(count));
        }
        finally
        {
            CultureInfo.CurrentUICulture = prevUi;
            CultureInfo.CurrentCulture = prev;
        }
    }

    [Theory]
    [InlineData(0, "0 item(s) needed no changes.")]
    [InlineData(1, "1 item(s) needed no changes.")]
    [InlineData(2, "2 item(s) needed no changes.")]
    public void NeededNoChanges_UsesCorrectPluralInEnglish(int count, string expected)
    {
        Assert.Equal(expected, SdkStrings.Summary_NeededNoChanges(count));
    }

    [Theory]
    [InlineData(0, "0 Elemente benötigten keine Änderungen.")]
    [InlineData(1, "1 Element benötigte keine Änderungen.")]
    [InlineData(2, "2 Elemente benötigten keine Änderungen.")]
    public void NeededNoChanges_UsesCorrectPluralInGerman(int count, string expected)
    {
        var prevUi = CultureInfo.CurrentUICulture;
        var prev = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("de-DE");
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal(expected, SdkStrings.Summary_NeededNoChanges(count));
        }
        finally
        {
            CultureInfo.CurrentUICulture = prevUi;
            CultureInfo.CurrentCulture = prev;
        }
    }

    [Fact]
    public void SizeUnits_UseCurrentCultureDecimalSeparator()
    {
        var prevUi = CultureInfo.CurrentUICulture;
        var prev = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("de-DE");
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            string[] suffixes = { SdkStrings.Unit_B, SdkStrings.Unit_KB, SdkStrings.Unit_MB, SdkStrings.Unit_GB, SdkStrings.Unit_TB };
        int i = 2; // MB
        double value = 1.5;

        string formatted = string.Create(CultureInfo.CurrentCulture, $"{value:0.##} {suffixes[i]}");

        Assert.Equal("1,5 MB", formatted);
        }
        finally
        {
            CultureInfo.CurrentUICulture = prevUi;
            CultureInfo.CurrentCulture = prev;
        }
    }

    [Fact]
    public void FormatBytes_UsesCurrentCultureDecimalSeparator()
    {
        var prevUi = CultureInfo.CurrentUICulture;
        var prev = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("de-DE");
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            long bytes = (long)(1.5 * 1024 * 1024);
        string formatted = PerFileAction<object>.FormatBytes(bytes);

        Assert.Equal("1,5 MB", formatted);
        }
        finally
        {
            CultureInfo.CurrentUICulture = prevUi;
            CultureInfo.CurrentCulture = prev;
        }
    }

    [Fact]
    public void GermanTranslations_DifferFromEnglish_ExceptAllowlist()
    {
        // Allowlist of proper names and formats that are the same in English and German
        var allowlist = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "PDF", "ZIP", "JSON", "CSV", "Markdown", "SQL", "MP3", "GIF", "JPEG", "PNG", "WebP", "Format", "Video",
            "B", "KB", "MB", "GB", "TB"
        };

        var properties = typeof(SdkStrings).GetProperties(BindingFlags.Public | BindingFlags.Static);
        foreach (var prop in properties)
        {
            if (prop.PropertyType == typeof(string))
            {
                string enValue = (string)prop.GetValue(null)!;
                string deValue;
                var prevUi = CultureInfo.CurrentUICulture;
                var prev = CultureInfo.CurrentCulture;
                try
                {
                    CultureInfo.CurrentUICulture = new CultureInfo("de-DE");
                    CultureInfo.CurrentCulture = new CultureInfo("de-DE");
                    deValue = (string)prop.GetValue(null)!;
                }
                finally
                {
                    CultureInfo.CurrentUICulture = prevUi;
                    CultureInfo.CurrentCulture = prev;
                }

                if (!allowlist.Contains(enValue))
                {
                    Assert.NotEqual(enValue, deValue);
                }
            }
        }

        // Also check methods (parameterized strings)
        var methods = typeof(SdkStrings).GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName && m.ReturnType == typeof(string));

        foreach (var method in methods)
        {
            var parameters = method.GetParameters();
            object[] args = parameters.Select(p => p.ParameterType == typeof(int) ? (object)2 : "TEST").ToArray();

            string enValue = (string)method.Invoke(null, args)!;
            string deValue;
            var prevUi2 = CultureInfo.CurrentUICulture;
            var prev2 = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentUICulture = new CultureInfo("de-DE");
                CultureInfo.CurrentCulture = new CultureInfo("de-DE");
                deValue = (string)method.Invoke(null, args)!;
            }
            finally
            {
                CultureInfo.CurrentUICulture = prevUi2;
                CultureInfo.CurrentCulture = prev2;
            }

            Assert.NotEqual(enValue, deValue);
        }
    }


}
