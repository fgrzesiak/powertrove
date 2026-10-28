using System.Security.Cryptography;

namespace PowerTrove.ReleaseTools;

/// <summary>Explicit offline owner operation. Never called by packaging, the app or the website.</summary>
public static class PackKeyCommand
{
    public static async Task ExportPublicAsync(string keyFile, string publicKeyFile, CancellationToken token = default)
    {
        using var key = PackBundleBuilder.LoadSigner(keyFile);
        await using var output = new FileStream(publicKeyFile, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true);
        await output.WriteAsync(System.Text.Encoding.UTF8.GetBytes(key.ExportSubjectPublicKeyInfoPem()), token);
    }

    public static async Task GenerateAsync(string privateKeyFile, string publicKeyFile, CancellationToken token = default)
    {
        string privatePath = Path.GetFullPath(privateKeyFile), publicPath = Path.GetFullPath(publicKeyFile);
        if (privatePath.Equals(publicPath, StringComparison.OrdinalIgnoreCase) || File.Exists(privatePath) || File.Exists(publicPath))
            throw new IOException("Use new, distinct key files. Existing keys are never replaced.");
        for (DirectoryInfo? directory = new DirectoryInfo(Path.GetDirectoryName(privatePath)!); directory is not null; directory = directory.Parent)
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) || File.Exists(Path.Combine(directory.FullName, ".git")) ||
                directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("The private key must be outside Git repositories and linked directories.");
        Directory.CreateDirectory(Path.GetDirectoryName(privatePath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(publicPath)!);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        bool ownedPrivate = false;
        bool ownedPublic = false;
        try
        {
            await using (var output = new FileStream(privatePath, options))
            {
                ownedPrivate = true;
                await output.WriteAsync(System.Text.Encoding.UTF8.GetBytes(key.ExportPkcs8PrivateKeyPem()), token);
                await output.FlushAsync(token); output.Flush(flushToDisk: true);
            }
            await using var publicOutput = new FileStream(publicPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true);
            ownedPublic = true;
            await publicOutput.WriteAsync(System.Text.Encoding.UTF8.GetBytes(key.ExportSubjectPublicKeyInfoPem()), token);
        }
        catch
        {
            if (ownedPublic) File.Delete(publicPath);
            if (ownedPrivate) File.Delete(privatePath);
            throw;
        }
    }
}
