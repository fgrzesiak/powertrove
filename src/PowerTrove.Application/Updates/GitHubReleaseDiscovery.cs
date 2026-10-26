using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace PowerTrove.Application.Updates;

/// <summary>Explicit checks only. No request in construction, no credentials, retry loop or asset access.</summary>
public sealed class GitHubReleaseDiscovery : IReleaseDiscovery, IDisposable
{
    public const int MaximumResponseBytes = 2 * 1024 * 1024;
    public const int MaximumPages = 3;
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private readonly HttpClient _client;
    private readonly TimeProvider _time;
    private DateTimeOffset? _retryAfter;
    private int _checking;

    public GitHubReleaseDiscovery(HttpMessageHandler handler, TimeProvider? timeProvider = null)
    {
        _client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        _time = timeProvider ?? TimeProvider.System;
    }

    public static GitHubReleaseDiscovery CreateDefault() => new(new HttpClientHandler
    {
        AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false, Credentials = null,
        DefaultProxyCredentials = null, MaxResponseHeadersLength = 16
    });

    public async Task<ReleaseCheckResult> CheckAsync(ReleaseVersion installedVersion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installedVersion);
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _checking, 1, 0) != 0) return new(ReleaseCheckStatus.Busy);
        try
        {
            if (_retryAfter > _time.GetUtcNow()) return new(ReleaseCheckStatus.RateLimited);
            using var timeout = new CancellationTokenSource(RequestTimeout, _time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            var token = linked.Token;
            ReleaseCandidate? best = null;
            int bytesLeft = MaximumResponseBytes;
            bool anyPublished = false, anyVersion = false;
            for (int page = 1; page <= MaximumPages; page++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, OwnerChannelConfiguration.Default.ReleaseApi.AbsoluteUri + "?per_page=100&page=" + page);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
                request.Headers.UserAgent.ParseAdd("PowerTrove/1.0");
                request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
                using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                {
                    _retryAfter = RetryTime(response);
                    return new(ReleaseCheckStatus.RateLimited);
                }
                if (response.StatusCode != HttpStatusCode.OK) return new(ReleaseCheckStatus.Unavailable);
                if (response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && remaining.Any(value => value == "0"))
                    _retryAfter = RetryTime(response);
                if (response.Content.Headers.ContentLength > bytesLeft) return new(ReleaseCheckStatus.InvalidData);
                using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                using var payload = new MemoryStream();
                var buffer = new byte[8192];
                while (true)
                {
                    int count = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, bytesLeft + 1)), token).ConfigureAwait(false);
                    if (count == 0) break;
                    bytesLeft -= count;
                    if (bytesLeft < 0) return new(ReleaseCheckStatus.InvalidData);
                    payload.Write(buffer, 0, count);
                }
                token.ThrowIfCancellationRequested();
                using var document = JsonDocument.Parse(payload.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
                var root = document.RootElement;
                token.ThrowIfCancellationRequested();
                if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() > 100) return new(ReleaseCheckStatus.InvalidData);
                foreach (var release in root.EnumerateArray())
                {
                    token.ThrowIfCancellationRequested();
                    CheckDuplicateProperties(release);
                    if (release.ValueKind != JsonValueKind.Object) return new(ReleaseCheckStatus.InvalidData);
                    bool draft = release.GetProperty("draft").GetBoolean();
                    bool prerelease = release.GetProperty("prerelease").GetBoolean();
                    if (draft) continue;
                    anyPublished = true;
                    string? tag = release.GetProperty("tag_name").GetString();
                    if (!ReleaseVersion.TryParse(tag, out var version)) continue;
                    anyVersion = true;
                    if (!installedVersion.IsPrerelease && (prerelease || version!.IsPrerelease)) continue;
                    string? pageUrl = release.GetProperty("html_url").GetString();
                    if (pageUrl is null || pageUrl.Length > 2048 || !Uri.TryCreate(pageUrl, UriKind.Absolute, out var uri) ||
                        !ReleaseSource.TryValidatePage(uri, out var canonical) || canonical!.AbsoluteUri != ReleaseSource.ForTag(tag!).AbsoluteUri)
                        return new(ReleaseCheckStatus.InvalidData);
                    if (version!.CompareTo(installedVersion) > 0 && (best is null || version.CompareTo(best.Version) > 0))
                        best = new(version!, canonical!);
                }
                bool more = HasNextPage(response);
                if (!more) return best is not null ? new(ReleaseCheckStatus.Available, best)
                    : anyPublished && !anyVersion ? new(ReleaseCheckStatus.InvalidData)
                    : new(anyPublished ? ReleaseCheckStatus.UpToDate : ReleaseCheckStatus.NoReleases);
                if (page == MaximumPages) return new(ReleaseCheckStatus.InvalidData);
                if (_retryAfter > _time.GetUtcNow()) return new(ReleaseCheckStatus.RateLimited);
            }
            return new(ReleaseCheckStatus.InvalidData);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new(ReleaseCheckStatus.TimedOut); }
        catch (HttpRequestException) { return new(ReleaseCheckStatus.Offline); }
        catch (IOException) { return new(ReleaseCheckStatus.Offline); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        { return new(ReleaseCheckStatus.InvalidData); }
        finally { Volatile.Write(ref _checking, 0); }
    }

    private DateTimeOffset RetryTime(HttpResponseMessage response)
    {
        var now = _time.GetUtcNow();
        var retry = now.AddMinutes(1);
        if (response.Headers.RetryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
        {
            try { retry = now + delta; }
            catch (ArgumentOutOfRangeException) { retry = DateTimeOffset.MaxValue; }
        }
        if (response.Headers.RetryAfter?.Date is { } date && date > retry) retry = date;
        if (response.Headers.TryGetValues("X-RateLimit-Reset", out var resets) &&
            long.TryParse(resets.FirstOrDefault(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var unix))
        {
            try { var reset = DateTimeOffset.FromUnixTimeSeconds(unix); if (reset > retry) retry = reset; }
            catch (ArgumentOutOfRangeException) { }
        }
        return retry;
    }

    private static bool HasNextPage(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Link", out var values)) return false;
        string links = string.Join(",", values);
        if (links.Length > 8192) throw new JsonException();
        // Do not follow a supplied URL: subsequent requests always use the same fixed endpoint.
        return links.Contains("rel=\"next\"", StringComparison.Ordinal);
    }

    private static void CheckDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject()) if (!names.Add(property.Name)) throw new JsonException();
    }
    public void Dispose() => _client.Dispose();
}
