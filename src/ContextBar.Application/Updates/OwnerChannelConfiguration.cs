namespace ContextBar.Application.Updates;

/// <summary>Owner-controlled release configuration, never populated from pack metadata or user identifiers.</summary>
public sealed class OwnerChannelConfiguration
{
    public const string Repository = "fgrzesiak/powertrove";
    // Hosting remains an owner release task. Null fails closed without an HTTP request.
    public static OwnerChannelConfiguration Default { get; } = new();
    public Uri ReleaseApi { get; } = new("https://api.github.com/repos/" + Repository + "/releases");
    public Uri? PackChannel { get; }
    public OwnerChannelConfiguration(Uri? packChannel = null)
    {
        if (packChannel is not null && (!packChannel.IsAbsoluteUri || packChannel.Scheme != Uri.UriSchemeHttps
            || !packChannel.IsDefaultPort || packChannel.UserInfo.Length != 0 || packChannel.Query.Length != 0
            || packChannel.Fragment.Length != 0 || !packChannel.AbsolutePath.EndsWith('/')))
            throw new ArgumentException("The owner channel must be a canonical HTTPS directory.", nameof(packChannel));
        PackChannel = packChannel;
    }
    public Uri PackResource(string relative)
    {
        if (PackChannel is null) throw new InvalidOperationException("Pack hosting is not configured.");
        var uri = new Uri(PackChannel, relative);
        if (uri.Scheme != Uri.UriSchemeHttps || uri.Authority != PackChannel.Authority
            || !uri.AbsolutePath.StartsWith(PackChannel.AbsolutePath, StringComparison.Ordinal)
            || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0) throw new ArgumentException("Invalid pack resource.");
        return uri;
    }
}
