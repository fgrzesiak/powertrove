using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using ContextBar.Abstractions;
using ContextBar.Core.Resources;
using ContextBar.Core.Search;
using ContextBar.Core.Selection;
using ContextBar.Core.Workflows;
using Xunit;

namespace ContextBar.Core.Tests.Localization;

public class LocalizationTests
{
    [Fact]
    public void GermanTranslations_CB083()
    {
        var prevUi = System.Globalization.CultureInfo.CurrentUICulture;
        var prev = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo("de-DE");
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal("Entfernen Sie die anderen ausgew\u00E4hlten Elemente.", ContextBar.Abstractions.Resources.SdkStrings.RemoveOtherItems);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentUICulture = prevUi;
            System.Globalization.CultureInfo.CurrentCulture = prev;
        }
    }

    [Fact]
    public void GermanTranslations_DifferFromEnglish_ExceptAllowlist()
    {
        var allowlist = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "PDF", "ZIP", "JSON", "CSV", "Markdown", "SQL", "MP3", "GIF", "JPEG", "PNG", "WebP", "Format", "Video", "Workflow '{0}':"
        };

        var properties = typeof(Strings).GetProperties(BindingFlags.Public | BindingFlags.Static);
        foreach (var prop in properties)
        {
            if (prop.PropertyType == typeof(string))
            {
                string enValue = ReadEnglish(() => (string)prop.GetValue(null)!);
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

        var methods = typeof(Strings).GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName && m.ReturnType == typeof(string));

        foreach (var method in methods)
        {
            var parameters = method.GetParameters();
            object[] args = parameters.Select(p => p.ParameterType == typeof(string)
                ? (object)"TEST"
                : Convert.ChangeType(2, p.ParameterType, CultureInfo.InvariantCulture)).ToArray();

            string enValue = ReadEnglish(() => (string)method.Invoke(null, args)!);
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

            if (!allowlist.Contains(enValue) && !allowlist.Contains(enValue.Replace("TEST", "{0}")))
            {
                Assert.NotEqual(enValue, deValue);
            }
        }
    }

    private static string ReadEnglish(Func<string> read)
    {
        var previousUi = CultureInfo.CurrentUICulture;
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            return read();
        }
        finally
        {
            CultureInfo.CurrentUICulture = previousUi;
            CultureInfo.CurrentCulture = previous;
        }
    }
}
