using System.Security.Cryptography;
using System.Text.Json;

namespace PowerTrove.ReleaseTools;

/// <summary>Prepares an embedded public trust anchor. Never generates keys, signs or publishes a release.</summary>
public static class PackDistributionCommand
{
    internal static async Task ValidateFileAsync(string path)
    {
        if (new FileInfo(path).Length > 32768) throw new InvalidDataException("Distribution configuration is too large.");
        string json = await File.ReadAllTextAsync(path);
        if (json.Contains("PRIVATE KEY", StringComparison.Ordinal)) throw new InvalidDataException("Only public trust material can be embedded.");
        using var document = JsonDocument.Parse(json);
        ValidateChannel(document.RootElement.GetProperty("Channel").GetString() ?? "");
        var keys = document.RootElement.GetProperty("TrustedKeys");
        if (keys.ValueKind != JsonValueKind.Array || keys.GetArrayLength() is < 1 or > 8)
            throw new InvalidDataException("Pack distribution requires owner public trust keys.");
        foreach (var value in keys.EnumerateArray())
        {
            byte[] bytes = Convert.FromBase64String(value.GetString() ?? "");
            using var key = ECDsa.Create(); key.ImportSubjectPublicKeyInfo(bytes, out int read);
            if (read != bytes.Length || key.KeySize != 256 || key.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7")
                throw new InvalidDataException("A P-256 public key is required.");
        }
    }

    public static async Task ExecuteAsync(string publicKeyFile, string channel, string destination, CancellationToken token = default)
    {
        var uri = ValidateChannel(channel);
        if (new FileInfo(publicKeyFile).Length > 16384) throw new InvalidDataException("Public key file is too large.");
        string pem = await File.ReadAllTextAsync(publicKeyFile, token);
        if (pem.Contains("PRIVATE KEY", StringComparison.Ordinal)) throw new InvalidDataException("Provide only the public key.");
        using var key = ECDsa.Create(); key.ImportFromPem(pem);
        if (key.KeySize != 256 || key.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7")
            throw new InvalidDataException("A P-256 public key is required.");
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true);
        await JsonSerializer.SerializeAsync(output, new
        {
            Channel = uri.AbsoluteUri,
            TrustedKeys = new[] { Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) }
        }, cancellationToken: token);
    }

    private static Uri ValidateChannel(string channel)
    {
        if (!Uri.TryCreate(channel, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || !uri.AbsolutePath.EndsWith('/'))
            throw new ArgumentException("A canonical HTTPS channel directory is required.", nameof(channel));
        if (uri.Host == "github.com" && uri.AbsoluteUri != "https://github.com/fgrzesiak/powertrove/releases/download/packs-v1/")
            throw new ArgumentException("Use the official packs-v1 GitHub release directory.", nameof(channel));
        return uri;
    }
}
