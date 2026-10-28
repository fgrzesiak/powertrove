namespace PowerTrove.Application.Updates;

/// <summary>Owner-controlled release configuration, never populated from pack metadata or user identifiers.</summary>
public sealed class OwnerChannelConfiguration
{
    public const string Repository = "fgrzesiak/powertrove";
    public const string GitHubPackChannel = "https://github.com/" + Repository + "/releases/download/packs-v1/";
    // Hosting remains an owner release task. Null fails closed without an HTTP request.
    public static OwnerChannelConfiguration Default { get; } = new(Packs.PackDistributionConfiguration.Current.Channel is { } channel ? new Uri(channel) : null);
    public Uri ReleaseApi { get; } = new("https://api.github.com/repos/" + Repository + "/releases");
    public Uri? PackChannel { get; }
    public bool UsesGitHubAssets => PackChannel?.AbsoluteUri == GitHubPackChannel;
    public OwnerChannelConfiguration(Uri? packChannel = null)
    {
        if (packChannel is not null && (!packChannel.IsAbsoluteUri || packChannel.Scheme != Uri.UriSchemeHttps
            || !packChannel.IsDefaultPort || packChannel.UserInfo.Length != 0 || packChannel.Query.Length != 0
            || packChannel.Fragment.Length != 0 || !packChannel.AbsolutePath.EndsWith('/')))
            throw new ArgumentException("The owner channel must be a canonical HTTPS directory.", nameof(packChannel));
        PackChannel = packChannel;
        if (packChannel?.Host == "github.com" && !UsesGitHubAssets)
            throw new ArgumentException("The GitHub pack channel must use the fixed official pack release.", nameof(packChannel));
    }
    public Uri PackResource(string relative)
    {
        if (PackChannel is null) throw new InvalidOperationException("Pack hosting is not configured.");
        if (UsesGitHubAssets)
        {
            relative = relative switch
            {
                "packs/catalog.json" => "catalog.json",
                "packs/catalog.json.sig" => "catalog.json.sig",
                _ => GitHubBundleName(relative)
            };
        }
        var uri = new Uri(PackChannel, relative);
        if (uri.Scheme != Uri.UriSchemeHttps || uri.Authority != PackChannel.Authority
            || !uri.AbsolutePath.StartsWith(PackChannel.AbsolutePath, StringComparison.Ordinal)
            || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0) throw new ArgumentException("Invalid pack resource.");
        return uri;
    }

    private static string GitHubBundleName(string relative)
    {
        var parts = relative.Split('/');
        if (parts.Length != 3 || parts[0] != "packs" ||
            !System.Text.RegularExpressions.Regex.IsMatch(parts[1], @"\A[a-z0-9]+(?:[.-][a-z0-9]+)*\z") ||
            !System.Text.RegularExpressions.Regex.IsMatch(parts[2], @"\A[0-9]+\.[0-9]+\.[0-9]+(?:\.[0-9]+)?\.cbpack\z"))
            throw new ArgumentException("Invalid GitHub pack resource.");
        return parts[1] + "-" + parts[2];
    }

    public bool AllowsPackRedirect(Uri uri) => UsesGitHubAssets && uri.IsAbsoluteUri &&
        uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 &&
        (uri.Host is "release-assets.githubusercontent.com" or "objects.githubusercontent.com" ||
            uri.Host == "github.com" && uri.Query.Length == 0 && uri.AbsolutePath.StartsWith(PackChannel!.AbsolutePath, StringComparison.Ordinal));
}
