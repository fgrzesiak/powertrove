using System.Reflection;
using System.Text.Json;

namespace PowerTrove.Application.Packs;

/// <summary>The trust anchor is embedded at release build time, never taken from downloaded metadata.</summary>
public sealed record PackDistributionConfiguration(string? Channel, string[] TrustedKeys)
{
    public static PackDistributionConfiguration Current { get; } = Read();
    public static Version HostVersion => Assembly.GetEntryAssembly()?.GetName().Version ??
        typeof(PackDistributionConfiguration).Assembly.GetName().Version ?? new Version(0, 1, 0);
    private static PackDistributionConfiguration Read()
    {
        try
        {
            using var stream = typeof(PackDistributionConfiguration).Assembly.GetManifestResourceStream("PowerTrove.PackDistribution.json");
            if (stream is null || stream.Length > 32768) return new(null, []);
            var config = JsonSerializer.Deserialize<PackDistributionConfiguration>(stream);
            if (config?.TrustedKeys is null || config.TrustedKeys.Length > 8) return new(null, []);
            if (config.Channel is not null && (!Uri.TryCreate(config.Channel, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
                uri.Query.Length != 0 || uri.Fragment.Length != 0 || !uri.AbsolutePath.EndsWith('/'))) return new(null, []);
            if (config.Channel is { } channel && new Uri(channel).Host == "github.com" && channel != Updates.OwnerChannelConfiguration.GitHubPackChannel)
                return new(null, []);
            return config;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or IOException) { return new(null, []); }
    }
}
