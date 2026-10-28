using System;
using System.Linq;
using System.Reflection;

namespace PowerTrove.Packs;

/// <summary>Release-selected pack identity, independent of the desktop/SDK assembly version.</summary>
internal static class PackReleaseVersion
{
    public static string Read(Assembly assembly, string fallback = "0.1.0")
    {
        var values = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Where(a => a.Key == "PowerTrove.PackVersion").Select(a => a.Value).ToArray();
        string version = values.Length == 0 ? fallback : values.Length == 1 ? values[0]! : throw new InvalidOperationException("Duplicate pack version metadata.");
        if (!Version.TryParse(version, out var parsed) || parsed.Build < 0 || parsed.Revision > 0)
            throw new InvalidOperationException("Invalid pack release version.");
        return version;
    }
}
