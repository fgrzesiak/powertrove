using System.Net;
using System.Security.Cryptography;

namespace PowerTrove.Application.Updates;

/// <summary>Downloads only after explicit consent; verifies the exact release asset before shell execution.</summary>
public sealed class GitHubReleaseInstaller : IReleaseInstaller, IDisposable
{
    public const long MaximumInstallerBytes = 1024L * 1024 * 1024;
    public static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);
    private readonly HttpClient _client;
    private readonly IReleaseInstallerLauncher _launcher;
    private readonly Func<bool> _canInstall;
    private readonly string _stagingRoot;
    private readonly TimeProvider _time;
    private int _installing;

    public GitHubReleaseInstaller(HttpMessageHandler handler, IReleaseInstallerLauncher launcher,
        string stagingRoot, Func<bool>? canInstall = null, TimeProvider? timeProvider = null)
    {
        _client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _launcher = launcher;
        _stagingRoot = stagingRoot;
        _canInstall = canInstall ?? (() => true);
        _time = timeProvider ?? TimeProvider.System;
    }

    public static GitHubReleaseInstaller CreateDefault(IReleaseInstallerLauncher launcher, string stagingRoot,
        Func<bool> canInstall) => new(new HttpClientHandler
        {
            AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false,
            Credentials = null, DefaultProxyCredentials = null, MaxResponseHeadersLength = 16
        }, launcher, stagingRoot, canInstall);

    public static string InstallerName(ReleaseVersion version) =>
        "PowerTrove-v" + version.Display.Split('-', '+')[0] + "-win-x64-installer.exe";

    public static bool IsValidAsset(ReleaseCandidate candidate)
    {
        if (!ReleaseSource.TryValidatePage(candidate.Page, out var page) || candidate.Installer is not { } asset ||
            !ReleaseVersion.TryParse(Uri.UnescapeDataString(page!.Segments[^1]), out var version) ||
            version!.CompareTo(candidate.Version) != 0 || asset.Size is <= 0 or > MaximumInstallerBytes ||
            asset.Sha256.Length != 64 || !asset.Sha256.All(char.IsAsciiHexDigit)) return false;
        string tag = Uri.UnescapeDataString(page.Segments[^1]);
        var expected = new Uri("https://github.com/" + ReleaseSource.Repository + "/releases/download/" +
            Uri.EscapeDataString(tag) + "/" + InstallerName(candidate.Version));
        return asset.Download.IsAbsoluteUri && asset.Download.AbsoluteUri == expected.AbsoluteUri;
    }

    public async Task<ReleaseInstallStatus> InstallAsync(ReleaseCandidate candidate, IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsValidAsset(candidate)) return ReleaseInstallStatus.InvalidData;
        if (Interlocked.CompareExchange(ref _installing, 1, 0) != 0) return ReleaseInstallStatus.Busy;
        string? directory = null;
        bool started = false;
        try
        {
            if (!_canInstall()) return ReleaseInstallStatus.Busy;
            using var timeout = new CancellationTokenSource(DownloadTimeout, _time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            directory = Path.Combine(_stagingRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, InstallerName(candidate.Version));
            // Capture the caller's UI context here, so the final idle check and launch cannot race a new action.
            await DownloadAsync(candidate.Installer!, path, progress, linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            if (!_canInstall()) return ReleaseInstallStatus.Busy;
            started = await _launcher.StartAsync(path, linked.Token);
            return started ? ReleaseInstallStatus.Started : ReleaseInstallStatus.Failed;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return ReleaseInstallStatus.TimedOut; }
        catch (HttpRequestException) { return ReleaseInstallStatus.Offline; }
        catch (InvalidDataException) { return ReleaseInstallStatus.InvalidData; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { return ReleaseInstallStatus.Failed; }
        finally
        {
            // A running installer owns its staged EXE until a later app launch cleans it up.
            if (!started && directory is not null) TryDelete(directory);
            Volatile.Write(ref _installing, 0);
        }
    }

    private async Task DownloadAsync(ReleaseInstallerAsset asset, string path, IProgress<int>? progress, CancellationToken token)
    {
        Uri uri = asset.Download;
        for (int redirect = 0; redirect <= 5; redirect++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("PowerTrove/1.0");
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or
                HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                if (redirect == 5 || response.Headers.Location is not { } location) throw new InvalidDataException();
                uri = new Uri(uri, location);
                if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
                    uri.Fragment.Length != 0 || uri.Host is not ("github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com"))
                    throw new InvalidDataException();
                continue;
            }
            if (response.StatusCode != HttpStatusCode.OK) throw new HttpRequestException();
            if (response.Content.Headers.ContentLength is { } length && length != asset.Size) throw new InvalidDataException();
            await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[81920];
            long total = 0;
            int previous = -1;
            while (true)
            {
                int read = await input.ReadAsync(buffer, token).ConfigureAwait(false);
                if (read == 0) break;
                total += read;
                if (total > asset.Size) throw new InvalidDataException();
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                int percent = (int)(total * 100 / asset.Size);
                if (percent != previous) { previous = percent; progress?.Report(percent); }
            }
            if (total != asset.Size || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(asset.Sha256)))
                throw new InvalidDataException();
            return;
        }
        throw new InvalidDataException();
    }

    /// <summary>Only our GUID staging directories, older than one day; never delete an active fresh download.</summary>
    public void CleanupStaleDownloads()
    {
        try
        {
            if (!Directory.Exists(_stagingRoot)) return;
            foreach (string directory in Directory.EnumerateDirectories(_stagingRoot))
                if (Guid.TryParseExact(Path.GetFileName(directory), "N", out _) &&
                    (File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0 &&
                    Directory.GetLastWriteTimeUtc(directory) < DateTime.UtcNow.AddDays(-1)) TryDelete(directory);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDelete(string directory)
    {
        try { Directory.Delete(directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    public void Dispose() => _client.Dispose();
}
