namespace PowerTrove.Architecture.Tests;

/// <summary>Repository-relative project rules, shared by real graph checks and negative controls.</summary>
internal static class PackDependencyRules
{
    public static bool Allows(string source, string target)
    {
        source = source.Replace('\\', '/');
        target = target.Replace('\\', '/');
        if (source.StartsWith("tests/", StringComparison.Ordinal) || source.Contains("/tests/", StringComparison.Ordinal)) return true;
        var from = source.Split('/');
        var to = target.Split('/');
        bool sdk = target == "src/PowerTrove.Abstractions/PowerTrove.Abstractions.csproj";
        bool tool = target.StartsWith("tools/", StringComparison.Ordinal);
        if (from.Length >= 4 && from[0] == "packs")
        {
            bool own = to.Length >= 4 && to[0] == "packs" && to[1] == from[1];
            return from[2] switch
            {
                "src" => sdk || tool,
                "adapters" => sdk || tool || (own && to[2] == "src"),
                "composition" => sdk || tool || (own && to[2] is "src" or "adapters"),
                _ => false
            };
        }
        if (from.Length >= 2 && from[0] == "src" && to.Length >= 3 && to[0] == "packs")
            return from[1] == "PowerTrove.Desktop" && to[2] == "composition";
        return true; // Other host/tool invariants are enforced by the existing dedicated rules.
    }
}
