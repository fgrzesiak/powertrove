using System.Net;
using System.Text;
using System.Text.Json;
using ContextBar.Application.Updates;

namespace ContextBar.Application.Tests;

public sealed class GitHubReleaseDiscoveryTests
{
    [Theory]
    [InlineData("1.0.0", "v1.10.0")]
    [InlineData("1.0.0-beta.1", "v3.0.0")]
    public async Task ExplicitCheck_SelectsHighestEligibleVersion_WithoutOpeningOrDownloadingAssets(string installed, string expected)
    {
        var handler = new Handler((_, _) => Task.FromResult(Json(new[]
        {
            Release("v1.9.0"), Release("v1.10.0"), Release("v99.0.0", draft: true), Release("v2.0.0-beta.2", prerelease: true),
            Release("v3.0.0", prerelease: true), Release("not-a-version")
        })));
        using var service = new GitHubReleaseDiscovery(handler);
        Assert.Equal(0, handler.Count);
        var result = await service.CheckAsync(Version(installed));
        Assert.Equal(ReleaseCheckStatus.Available, result.Status); Assert.Equal(ReleaseSource.ForTag(expected), result.Candidate!.Page);
        Assert.Equal(1, handler.Count); Assert.Equal(ReleaseSource.ApiBase + "?per_page=100&page=1", handler.Uris[0].AbsoluteUri);
        Assert.False(handler.HadAuthorization); Assert.False(handler.HadCookie);
    }

    [Theory]
    [InlineData("0.1.0-alpha.1+aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "0.1.0-alpha.10")]
    [InlineData("0.1.0-alpha.10+aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", null)]
    [InlineData("0.1.0", null)]
    public async Task AlphaPackageIdentity_SelectsLaterAlphaAndKeepsStableChannelSeparate(string installed, string? expected)
    {
        using var service = new GitHubReleaseDiscovery(new Handler((_, _) => Task.FromResult(Json(new[]
        {
            Release("v0.1.0-alpha.1", prerelease: true),
            Release("v0.1.0-alpha.2", prerelease: true),
            Release("v0.1.0-alpha.10", prerelease: true),
            Release("v0.1.0-alpha.99", prerelease: true, draft: true)
        }))));
        var result = await service.CheckAsync(Version(installed));
        Assert.Equal(expected is null ? ReleaseCheckStatus.UpToDate : ReleaseCheckStatus.Available, result.Status);
        Assert.Equal(expected, result.Candidate?.Version.Display);
    }

    [Fact]
    public async Task AlphaPackageIdentity_CanAdvanceToStableRelease()
    {
        using var service = new GitHubReleaseDiscovery(new Handler((_, _) =>
            Task.FromResult(Json(new[] { Release("v0.1.0") }))));
        var result = await service.CheckAsync(Version("0.1.0-alpha.10+aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
        Assert.Equal(ReleaseCheckStatus.Available, result.Status);
        Assert.Equal("0.1.0", result.Candidate!.Version.Display);
    }

    [Fact]
    public async Task Pagination_UsesFixedEndpoint_AndDoesNotTrustLinkHost()
    {
        var handler = new Handler((_, _) =>
        {
            var response = Json(new[] { Release("v1.1.0") });
            response.Headers.TryAddWithoutValidation("Link", "<https://evil.test/releases?page=2>; rel=\"next\"");
            return Task.FromResult(response);
        });
        using var service = new GitHubReleaseDiscovery(handler);
        Assert.Equal(ReleaseCheckStatus.InvalidData, (await service.CheckAsync(Version("1.0.0"))).Status);
        Assert.Equal(GitHubReleaseDiscovery.MaximumPages, handler.Count);
        Assert.All(handler.Uris, uri => Assert.StartsWith(ReleaseSource.ApiBase, uri.AbsoluteUri));
    }

    [Theory]
    [InlineData("[]", ReleaseCheckStatus.NoReleases)]
    [InlineData("{}", ReleaseCheckStatus.InvalidData)]
    [InlineData("{broken", ReleaseCheckStatus.InvalidData)]
    [InlineData("[{\"draft\":false}]", ReleaseCheckStatus.InvalidData)]
    [InlineData("[{\"draft\":false,\"draft\":true,\"prerelease\":false}]", ReleaseCheckStatus.InvalidData)]
    public async Task BadOrEmptyMetadata_HasSafeTypedStatuses(string json, ReleaseCheckStatus expected)
    {
        using var service = new GitHubReleaseDiscovery(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(json) })));
        Assert.Equal(expected, (await service.CheckAsync(Version("1.0.0"))).Status);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, ReleaseCheckStatus.Unavailable)]
    [InlineData(HttpStatusCode.Found, ReleaseCheckStatus.Unavailable)]
    [InlineData(HttpStatusCode.ServiceUnavailable, ReleaseCheckStatus.Unavailable)]
    [InlineData(HttpStatusCode.Forbidden, ReleaseCheckStatus.RateLimited)]
    [InlineData(HttpStatusCode.TooManyRequests, ReleaseCheckStatus.RateLimited)]
    public async Task NonSuccessResponses_DoNotRedirectOrRetry(HttpStatusCode status, ReleaseCheckStatus expected)
    {
        var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(status) { Headers = { Location = new Uri("https://evil.test") } }));
        using var service = new GitHubReleaseDiscovery(handler);
        Assert.Equal(expected, (await service.CheckAsync(Version("1.0.0"))).Status); Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async Task RateLimitReset_IsRespectedAcrossExplicitChecks_WithoutAnAutomaticRetry()
    {
        var clock = new Clock();
        var handler = new Handler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            response.Headers.Add("X-RateLimit-Reset", clock.GetUtcNow().AddMinutes(2).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
            return Task.FromResult(response);
        });
        using var service = new GitHubReleaseDiscovery(handler, clock);
        Assert.Equal(ReleaseCheckStatus.RateLimited, (await service.CheckAsync(Version("1.0.0"))).Status);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(ReleaseCheckStatus.RateLimited, (await service.CheckAsync(Version("1.0.0"))).Status); Assert.Equal(1, handler.Count);
        clock.Advance(TimeSpan.FromMinutes(1)); Assert.Equal(1, handler.Count);
        await service.CheckAsync(Version("1.0.0")); Assert.Equal(2, handler.Count);
    }

    [Fact]
    public async Task SuccessfulLastPermittedResponse_PreservesTheResetForLaterChecks()
    {
        var clock = new Clock();
        var handler = new Handler((_, _) =>
        {
            var response = Json(Array.Empty<object>());
            response.Headers.Add("X-RateLimit-Remaining", "0");
            response.Headers.Add("X-RateLimit-Reset", clock.GetUtcNow().AddMinutes(2).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
            return Task.FromResult(response);
        });
        using var service = new GitHubReleaseDiscovery(handler, clock);
        Assert.Equal(ReleaseCheckStatus.NoReleases, (await service.CheckAsync(Version("1.0.0"))).Status);
        Assert.Equal(ReleaseCheckStatus.RateLimited, (await service.CheckAsync(Version("1.0.0"))).Status);
        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async Task CompletedPagination_SelectsAcrossPages_AndCurrentVersionsAreNotOffered()
    {
        var handler = new Handler((request, _) =>
        {
            var response = Json(new[] { Release(request.RequestUri!.Query.EndsWith("page=1", StringComparison.Ordinal) ? "v1.1.0" : "v1.10.0") });
            if (request.RequestUri.Query.EndsWith("page=1", StringComparison.Ordinal)) response.Headers.TryAddWithoutValidation("Link", "<ignored>; rel=\"next\"");
            return Task.FromResult(response);
        });
        using var service = new GitHubReleaseDiscovery(handler);
        var result = await service.CheckAsync(Version("1.0.0")); Assert.Equal("1.10.0", result.Candidate!.Version.Display); Assert.Equal(2, handler.Count);
        Assert.Equal(ReleaseCheckStatus.UpToDate, (await service.CheckAsync(Version("1.10.0"))).Status);
    }

    [Fact]
    public async Task TimeoutCancellationOfflineAndConcurrentChecks_AreBounded()
    {
        var clock = new Clock();
        var handler = new Handler(async (_, token) => { await Task.Delay(Timeout.InfiniteTimeSpan, token); return Json(Array.Empty<object>()); });
        using var service = new GitHubReleaseDiscovery(handler, clock);
        var check = service.CheckAsync(Version("1.0.0")); Assert.False(check.IsCompleted);
        Assert.Equal(ReleaseCheckStatus.Busy, (await service.CheckAsync(Version("1.0.0"))).Status);
        clock.Advance(GitHubReleaseDiscovery.RequestTimeout);
        Assert.Equal(ReleaseCheckStatus.TimedOut, (await check).Status);
        using var cts = new CancellationTokenSource();
        var canceled = service.CheckAsync(Version("1.0.0"), cts.Token); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        using var offline = new GitHubReleaseDiscovery(new Handler((_, _) => throw new HttpRequestException("private network details")));
        Assert.Equal(ReleaseCheckStatus.Offline, (await offline.CheckAsync(Version("1.0.0"))).Status);
    }

    [Fact]
    public async Task OversizedBodies_AreRejectedWithAndWithoutContentLength()
    {
        using var declared = new GitHubReleaseDiscovery(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new ByteArrayContent(new byte[GitHubReleaseDiscovery.MaximumResponseBytes + 1]) })));
        Assert.Equal(ReleaseCheckStatus.InvalidData, (await declared.CheckAsync(Version("1.0.0"))).Status);
        var stream = new OversizedStream();
        using var chunked = new GitHubReleaseDiscovery(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) })));
        Assert.Equal(ReleaseCheckStatus.InvalidData, (await chunked.CheckAsync(Version("1.0.0"))).Status);
        Assert.Equal(GitHubReleaseDiscovery.MaximumResponseBytes + 1, stream.ReadBytes);
    }

    [Fact]
    public async Task UnsafeReleasePage_IsRejected_EvenWhenTheVersionIsNewer()
    {
        var release = new { tag_name = "v2.0.0", draft = false, prerelease = false, html_url = "https://evil.test/setup.exe" };
        using var service = new GitHubReleaseDiscovery(new Handler((_, _) => Task.FromResult(Json(new[] { release }))));
        var result = await service.CheckAsync(Version("1.0.0")); Assert.Equal(ReleaseCheckStatus.InvalidData, result.Status); Assert.Null(result.Candidate);
    }

    private sealed record TestRelease(string tag_name, bool draft, bool prerelease, string html_url);
    private static TestRelease Release(string tag, bool draft = false, bool prerelease = false) => new(tag, draft, prerelease,
        ReleaseVersion.TryParse(tag, out _) ? ReleaseSource.ForTag(tag).AbsoluteUri : "https://github.com/fgrzesiak/powertrove/releases");
    internal static ReleaseVersion Version(string text) { Assert.True(ReleaseVersion.TryParse(text, out var version)); return version!; }
    private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Count { get; private set; }
        public bool HadAuthorization { get; private set; }
        public bool HadCookie { get; private set; }
        public List<Uri> Uris { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++; Uris.Add(request.RequestUri!); HadAuthorization |= request.Headers.Authorization is not null;
            HadCookie |= request.Headers.Contains("Cookie"); return send(request, cancellationToken);
        }
    }
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private readonly List<ClockTimer> _timers = new();
        public override DateTimeOffset GetUtcNow() => _now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ClockTimer(this, callback, state); timer.Change(dueTime, period); _timers.Add(timer); return timer;
        }
        public void Advance(TimeSpan time) { _now += time; foreach (var timer in _timers.ToArray()) timer.Fire(); }
        private sealed class ClockTimer(Clock clock, TimerCallback callback, object? state) : ITimer
        {
            private bool _disposed;
            private DateTimeOffset _due;
            public bool Change(TimeSpan dueTime, TimeSpan period) { _due = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : clock.GetUtcNow() + dueTime; return !_disposed; }
            public void Fire() { if (!_disposed && _due <= clock.GetUtcNow()) { _disposed = true; callback(state); } }
            public void Dispose() => _disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
    private sealed class OversizedStream : Stream
    {
        public int ReadBytes { get; private set; }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) { Array.Fill(buffer, (byte)' ', offset, count); ReadBytes += count; return count; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) { ct.ThrowIfCancellationRequested(); buffer.Span.Fill((byte)' '); ReadBytes += buffer.Length; return ValueTask.FromResult(buffer.Length); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
