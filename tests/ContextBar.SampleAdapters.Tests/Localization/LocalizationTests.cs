using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using ContextBar.SampleAdapters.Resources;
using Xunit;

namespace ContextBar.SampleAdapters.Tests.Localization;

public class LocalizationTests
{
    [Fact]
    public void GermanTranslations_DifferFromEnglish_ExceptAllowlist()
    {
        var allowlist = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "PDF", "ZIP", "JSON", "CSV", "Markdown", "SQL", "MP3", "GIF", "JPEG", "PNG", "WebP", "Format", "Video"
        };

        var properties = typeof(Strings).GetProperties(BindingFlags.Public | BindingFlags.Static);
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

        var methods = typeof(Strings).GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
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

            if (!allowlist.Contains(enValue) && !allowlist.Contains(enValue.Replace("TEST", "{0}")))
            {
                Assert.NotEqual(enValue, deValue);
            }
        }
    }
}
