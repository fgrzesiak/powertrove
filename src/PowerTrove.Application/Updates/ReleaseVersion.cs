using System.Numerics;

namespace PowerTrove.Application.Updates;

/// <summary>SemVer precedence, also accepting the four numeric components used by .NET assemblies.</summary>
public sealed class ReleaseVersion : IComparable<ReleaseVersion>
{
    private readonly BigInteger[] _numbers;
    private readonly string[] _prerelease;
    public bool IsPrerelease => _prerelease.Length > 0;
    public string Display { get; }
    private ReleaseVersion(BigInteger[] numbers, string[] prerelease, string display)
    { _numbers = numbers; _prerelease = prerelease; Display = display; }

    public static bool TryParse(string? value, out ReleaseVersion? version)
    {
        version = null;
        if (string.IsNullOrEmpty(value) || value.Length > 128) return false;
        string text = value[0] is 'v' or 'V' ? value[1..] : value;
        string[] build = text.Split('+');
        if (build.Length > 2 || build.Length == 2 && !Identifiers(build[1], numericLeadingZeros: true)) return false;
        int dash = build[0].IndexOf('-');
        string core = dash < 0 ? build[0] : build[0][..dash];
        string[] prerelease = dash < 0 ? Array.Empty<string>() : build[0][(dash + 1)..].Split('.');
        if (dash >= 0 && !Identifiers(build[0][(dash + 1)..], numericLeadingZeros: false)) return false;
        string[] components = core.Split('.');
        if (components.Length is < 3 or > 4) return false;
        var numbers = new BigInteger[4];
        for (int i = 0; i < components.Length; i++)
        {
            string component = components[i];
            if (!Digits(component) || component.Length > 1 && component[0] == '0') return false;
            numbers[i] = BigInteger.Parse(component, System.Globalization.CultureInfo.InvariantCulture);
        }
        version = new ReleaseVersion(numbers, prerelease, text);
        return true;
    }

    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null) return 1;
        for (int i = 0; i < 4; i++)
        {
            int comparison = _numbers[i].CompareTo(other._numbers[i]);
            if (comparison != 0) return comparison;
        }
        if (!IsPrerelease || !other.IsPrerelease) return IsPrerelease == other.IsPrerelease ? 0 : IsPrerelease ? -1 : 1;
        for (int i = 0; i < Math.Min(_prerelease.Length, other._prerelease.Length); i++)
        {
            string left = _prerelease[i], right = other._prerelease[i];
            bool leftNumeric = Digits(left), rightNumeric = Digits(right);
            int comparison = leftNumeric && rightNumeric ? left.Length.CompareTo(right.Length) : leftNumeric == rightNumeric ? 0 : leftNumeric ? -1 : 1;
            if (comparison == 0) comparison = string.Compare(left, right, StringComparison.Ordinal);
            if (comparison != 0) return comparison;
        }
        return _prerelease.Length.CompareTo(other._prerelease.Length);
    }

    private static bool Digits(string value) => value.Length > 0 && value.All(char.IsAsciiDigit);
    private static bool Identifiers(string value, bool numericLeadingZeros) => value.Split('.').All(part =>
        part.Length > 0 && part.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') &&
        (numericLeadingZeros || !Digits(part) || part.Length == 1 || part[0] != '0'));
    public override string ToString() => Display;
}
