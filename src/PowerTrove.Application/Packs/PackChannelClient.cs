using System.Net;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using PowerTrove.Abstractions.Manifest;
using PowerTrove.Application.Updates;
using PowerTrove.Core.Catalog;

namespace PowerTrove.Application.Packs;

public enum PackChannelStatus { Ready, Installed, NotConfigured, Offline, TimedOut, InvalidData, Unavailable, Busy, Incompatible }
public sealed record AvailablePack(ActionManifest Manifest, long DownloadBytes, bool IsCompatible);
public sealed record PackCatalogResult(PackChannelStatus Status, IReadOnlyList<AvailablePack> Packs);
public sealed record PackDownloadProgress(long CompletedBytes, long? TotalBytes);
public sealed record PackDownloadResult(PackChannelStatus Status, InstalledPack? Installed = null);
public sealed record PackInstallOffer(string Id, string Version, string Name, string Publisher, long DownloadBytes, string ManifestFingerprint);
public sealed record PackOfferResult(PackChannelStatus Status, PackInstallOffer? Offer = null);

/// <summary>Anonymous signed-catalog checks; downloads and installation require explicit user requests.</summary>
public sealed class PackChannelClient : IDisposable
{
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private readonly OwnerChannelConfiguration _configuration;
    private readonly HttpClient _client;
    private readonly PackSignatureVerifier _signatures;
    private readonly PackInstaller _installer;
    private readonly IPackInstallFileSystem _files;
    private readonly Version _hostVersion;
    private readonly TimeProvider _clock;
    private readonly string _downloads;
    private readonly string _cache;
    private IReadOnlyList<AvailablePack> _catalog = [];
    private bool _hasCatalog;
    public bool HasCatalog => _hasCatalog;
    public bool IsConfigured => _configuration.PackChannel is not null && _signatures.IsConfigured;
    public string? CatalogJson { get; private set; }
    private int _busy;
    public IReadOnlyList<AvailablePack> CachedPacks => _catalog;

    public PackChannelClient(OwnerChannelConfiguration configuration, HttpMessageHandler handler, PackInstaller installer,
        string userRoot, Version hostVersion, PackSignatureVerifier? signatures = null, IPackInstallFileSystem? files = null,
        TimeProvider? clock = null)
    {
        _configuration = configuration; _installer = installer; _signatures = signatures ?? new(); _files = files ?? new PhysicalPackInstallFileSystem();
        _hostVersion = hostVersion; _clock = clock ?? TimeProvider.System;
        _cache = Path.Combine(PackOutputCommitter.Canonical(userRoot), "pack-catalog.cache");
        _downloads = Path.Combine(PackOutputCommitter.Canonical(userRoot), "downloads");
        _client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }
    public static PackChannelClient CreateDefault(PackInstaller installer, string userRoot, Version hostVersion) =>
        new(OwnerChannelConfiguration.Default, new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false,
            UseDefaultCredentials = false, Credentials = null, DefaultProxyCredentials = null, MaxResponseHeadersLength = 16 }, installer, userRoot, hostVersion);

    public Task<PackCatalogResult> RefreshOnUserRequestAsync(CancellationToken token = default) => RefreshAsync(token);

    /// <summary>Metadata only. Sends no installed-pack list and never downloads pack payloads.</summary>
    public Task<PackCatalogResult> RefreshAutomaticallyAsync(CancellationToken token = default) => RefreshAsync(token);

    private async Task<PackCatalogResult> RefreshAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!IsConfigured) return new(PackChannelStatus.NotConfigured, []);
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return new(PackChannelStatus.Busy, _catalog);
        try { return await FetchCatalogAsync(token).ConfigureAwait(false); }
        finally { Volatile.Write(ref _busy, 0); }
    }

    private async Task<PackCatalogResult> FetchCatalogAsync(CancellationToken token)
    {
        // A failed refresh does not erase a previously verified compatible catalog.
        try
        {
            using var timeout = new CancellationTokenSource(RequestTimeout, _clock);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
            byte[] bytes = await GetBytesAsync(_configuration.PackResource("packs/catalog.json"), ActionCatalog.MaximumJsonBytes, linked.Token).ConfigureAwait(false);
            byte[] signature = await GetBytesAsync(_configuration.PackResource("packs/catalog.json.sig"), 128, linked.Token).ConfigureAwait(false);
            ReadCatalog(bytes, signature);
            await SaveCacheAsync(bytes, signature, linked.Token).ConfigureAwait(false);
            return new(PackChannelStatus.Ready, _catalog);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return new(PackChannelStatus.TimedOut, []); }
        catch (Exception ex) when (ex is HttpRequestException or IOException && ex is not InvalidDataException) { return new(PackChannelStatus.Offline, []); }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or CatalogFormatException or InvalidOperationException
            or KeyNotFoundException or FormatException or OverflowException or ArgumentException or DecoderFallbackException)
        { return new(PackChannelStatus.InvalidData, []); }
    }

    private void ReadCatalog(byte[] bytes, byte[] signature)
    {
        if (!_signatures.Verify(bytes, signature)) throw new InvalidDataException();
        string json = new UTF8Encoding(false, true).GetString(bytes);
        // Reuse the strict catalog shape, duplicate/action-ID checks and existing limits.
        _ = ActionCatalog.Read(json, [], _hostVersion, "windows", requireWorkerInput: true);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 40 });
        var packs = new List<AvailablePack>();
        foreach (var item in document.RootElement.GetProperty("packs").EnumerateArray())
        {
            var manifest = PackBundleVerifier.ParseManifest(Encoding.UTF8.GetBytes(item.GetProperty("manifest").GetRawText()), _hostVersion, requireCompatible: false);
            long download = item.GetProperty("downloadBytes").GetInt64();
            if (download > checked(manifest.Payload!.SizeBytes + 1024 * 1024)) throw new InvalidDataException();
            packs.Add(new AvailablePack(manifest, download, PackCompatibility.IsSupported(manifest, _hostVersion, "windows", requireWorkerInput: true)));
        }
        _catalog = packs.AsReadOnly(); CatalogJson = json; _hasCatalog = true;

    }
    /// <summary>Verified local metadata only. Never initiates a network request.</summary>
    public async Task<PackChannelStatus> LoadCachedAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return PackChannelStatus.Busy;
        try
        {
            if (!_files.FileExists(_cache)) return PackChannelStatus.Unavailable;
            await using var input = _files.OpenRead(_cache);
            long cacheLength = input.Length;
            if (cacheLength is < 6 or > ActionCatalog.MaximumJsonBytes + 132L) return PackChannelStatus.InvalidData;
            byte[] header = new byte[4]; await input.ReadExactlyAsync(header, token).ConfigureAwait(false);
            int length = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header);
            if (length <= 0 || length > ActionCatalog.MaximumJsonBytes || cacheLength - 4 - length is <= 0 or > 128) return PackChannelStatus.InvalidData;
            byte[] bytes = new byte[length]; await input.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
            byte[] signature = new byte[(int)(cacheLength - input.Position)]; await input.ReadExactlyAsync(signature, token).ConfigureAwait(false);
            if (await input.ReadAsync(new byte[1], token).ConfigureAwait(false) != 0) return PackChannelStatus.InvalidData;
            ReadCatalog(bytes, signature);
            return PackChannelStatus.Ready;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or CatalogFormatException or ArgumentException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        { return PackChannelStatus.InvalidData; }
        finally { Volatile.Write(ref _busy, 0); }
    }
    private async Task SaveCacheAsync(byte[] bytes, byte[] signature, CancellationToken token)
    {
        string temporary = _cache + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            _files.CreateDirectory(Path.GetDirectoryName(_cache)!);
            await using (var output = _files.CreateNew(temporary))
            {
                byte[] header = new byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
                await output.WriteAsync(header, token).ConfigureAwait(false);
                await output.WriteAsync(bytes, token).ConfigureAwait(false);
                await output.WriteAsync(signature, token).ConfigureAwait(false);
                await output.FlushAsync(token).ConfigureAwait(false);
            }
            _files.ActivateFile(temporary, _cache);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        finally { try { _files.DeleteFile(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    public async Task<PackDownloadResult> InstallOnUserRequestAsync(string packId, IProgress<PackDownloadProgress>? progress = null,
        CancellationToken token = default, PackInstallOffer? approvedOffer = null)
    {
        token.ThrowIfCancellationRequested(); PackInstaller.ValidateId(packId);
        if (!IsConfigured) return new(PackChannelStatus.NotConfigured);
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return new(PackChannelStatus.Busy);
        string? temporary = null;
        try
        {
            if (!_hasCatalog)
            {
                var refreshed = await FetchCatalogAsync(token).ConfigureAwait(false);
                if (refreshed.Status != PackChannelStatus.Ready) return new(refreshed.Status);
            }
            var pack = SelectPack(packId);
            if (pack is null) return new(PackChannelStatus.InvalidData);
            if (!pack.IsCompatible) return new(PackChannelStatus.Incompatible);
            if (approvedOffer is not null && approvedOffer != Offer(pack)) return new(PackChannelStatus.InvalidData);
            long maximum = checked(pack.Manifest.Payload!.SizeBytes + 1024 * 1024);
            if (pack.DownloadBytes > 0) maximum = Math.Min(maximum, pack.DownloadBytes);
            Uri uri = _configuration.PackResource("packs/" + Uri.EscapeDataString(pack.Manifest.Id) + "/" + Uri.EscapeDataString(pack.Manifest.Version) + ".cbpack");
            using var response = await SendAsync(uri, token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK) return new(PackChannelStatus.Unavailable);
            if (response.Content.Headers.ContentLength > maximum) return new(PackChannelStatus.InvalidData);
            _files.CreateDirectory(_downloads);
            temporary = Path.Combine(_downloads, Guid.NewGuid().ToString("N") + ".cbpack");
            long received = 0;
            await using (var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
            await using (var output = _files.CreateNew(temporary))
            {
                byte[] buffer = new byte[81920];
                while (true)
                {
                    int count = await ReadAsync(input, buffer.AsMemory(0, (int)Math.Min(buffer.Length, maximum - received + 1)), token).ConfigureAwait(false);
                    if (count == 0) break;
                    received = checked(received + count);
                    if (received > maximum) return new(PackChannelStatus.InvalidData);
                    await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                    progress?.Report(new PackDownloadProgress(received, pack.DownloadBytes > 0 ? pack.DownloadBytes : null));
                }
                await output.FlushAsync(token).ConfigureAwait(false);
            }
            if (pack.DownloadBytes > 0 && pack.DownloadBytes != received) return new(PackChannelStatus.InvalidData);
            var installed = await _installer.InstallAsync(temporary, token, pack.Manifest).ConfigureAwait(false);
            return new(PackChannelStatus.Installed, installed);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return new(PackChannelStatus.TimedOut); }
        catch (Exception ex) when (ex is HttpRequestException or IOException && ex is not InvalidDataException) { return new(PackChannelStatus.Offline); }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
        { return new(PackChannelStatus.InvalidData); }
        finally
        {
            try { if (temporary is not null) _files.DeleteFile(temporary); }
            finally { Volatile.Write(ref _busy, 0); }
        }
    }

    public async Task<PackOfferResult> PrepareOnUserRequestAsync(string packId, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested(); PackInstaller.ValidateId(packId);
        if (!IsConfigured) return new(PackChannelStatus.NotConfigured);
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return new(PackChannelStatus.Busy);
        try
        {
            if (!_hasCatalog)
            {
                var result = await FetchCatalogAsync(token).ConfigureAwait(false);
                if (result.Status != PackChannelStatus.Ready) return new(result.Status);
            }
            var pack = SelectPack(packId);
            return pack is null ? new(PackChannelStatus.InvalidData) : !pack.IsCompatible ? new(PackChannelStatus.Incompatible)
                : new(PackChannelStatus.Ready, Offer(pack));
        }
        finally { Volatile.Write(ref _busy, 0); }
    }
    private AvailablePack? SelectPack(string id) => _catalog.Where(p => p.Manifest.Id == id)
        .OrderByDescending(p => p.IsCompatible).ThenByDescending(p => Version.Parse(p.Manifest.Version)).FirstOrDefault();
    public AvailablePack? GetLatestCompatible(string id) => _catalog.Where(p => p.Manifest.Id == id && p.IsCompatible)
        .OrderByDescending(p => Version.Parse(p.Manifest.Version)).FirstOrDefault();
    private static PackInstallOffer Offer(AvailablePack pack) => new(pack.Manifest.Id, pack.Manifest.Version, ManifestLocalization.ForCulture(pack.Manifest).DisplayName,
        pack.Manifest.Publisher, pack.DownloadBytes, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ActionPackManifest.ToJson(pack.Manifest)))));

    private async Task<HttpResponseMessage> SendAsync(Uri uri, CancellationToken token)
    {
        using var timeout = new CancellationTokenSource(RequestTimeout, _clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        for (int redirects = 0; ; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("PowerTrove/1.0");
            var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
            if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Found or
                HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)) return response;
            try
            {
                if (redirects == 5 || response.Headers.Location is not { } location) throw new InvalidDataException();
                var target = new Uri(uri, location);
                if (!_configuration.AllowsPackRedirect(target)) throw new InvalidDataException();
                uri = target;
            }
            finally { response.Dispose(); }
        }
    }
    private async Task<byte[]> GetBytesAsync(Uri uri, int maximum, CancellationToken token)
    {
        using var response = await SendAsync(uri, token).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength > maximum) throw new InvalidDataException();
        await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream(); byte[] buffer = new byte[8192];
        while (true)
        {
            int count = await ReadAsync(input, buffer.AsMemory(0, (int)Math.Min(buffer.Length, maximum - output.Length + 1)), token).ConfigureAwait(false);
            if (count == 0) break;
            if (output.Length + count > maximum) throw new InvalidDataException();
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }
    private async Task<int> ReadAsync(Stream stream, Memory<byte> buffer, CancellationToken token)
    {
        using var timeout = new CancellationTokenSource(RequestTimeout, _clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        return await stream.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
    }
    public void Dispose() => _client.Dispose();
}
