using System.Net;
using System.Net.Http.Headers;
using BarakoCMS.Analytics.Umami.Features;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests.Features.Analytics;

/// <summary>
/// The server-side referrer favicon (#677): which names are accepted, and what the fetch will and
/// will not take. A stub handler stands in for the internet.
/// </summary>
public class FaviconTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.5")]
    [InlineData("localhost")]
    [InlineData("localhost:5000")]
    [InlineData("example.com:8080")]
    [InlineData("https://example.com")]
    [InlineData("example.com/path")]
    [InlineData("user:pw@example.com")]
    [InlineData("[::1]")]
    [InlineData("exa mple.com")]
    [InlineData("-bad.example.com")]
    [InlineData("example..com")]
    [InlineData("example.com.")]
    [InlineData("")]
    public void The_validator_rejects_anything_that_is_not_a_plain_hostname(string domain)
    {
        new FaviconRequestValidator().Validate(new FaviconRequest { Domain = domain }).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Non_ascii_is_rejected_before_it_can_lowercase_into_ascii()
    {
        FaviconHostname.TryNormalize("\u212A.example.com", out _).Should().BeFalse();
        FaviconHostname.TryNormalize("b\u00FCcher.example", out _).Should().BeFalse();
    }

    [Fact]
    public void The_validator_rejects_an_over_long_name()
    {
        var name = string.Join('.', Enumerable.Repeat(new string('a', 60), 5)) + ".com";
        name.Length.Should().BeGreaterThan(FaviconHostname.MaxLength);

        new FaviconRequestValidator().Validate(new FaviconRequest { Domain = name }).IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData("example.com", "example.com")]
    [InlineData("News.Example.CO.uk", "news.example.co.uk")]
    [InlineData("xn--bcher-kva.example", "xn--bcher-kva.example")]
    public void A_plain_hostname_is_accepted_and_lowercased(string domain, string expected)
    {
        FaviconHostname.TryNormalize(domain, out var host).Should().BeTrue();
        host.Should().Be(expected);
    }

    [Fact]
    public async Task A_small_image_is_returned_with_its_type_and_fetched_once_for_a_day()
    {
        var handler = new StubHandler(_ => Respond(HttpStatusCode.OK, "image/x-icon", new byte[] { 1, 2, 3 }));
        using var fetcher = new FaviconFetcher(new StubFactory(handler));

        var first = await fetcher.GetAsync("example.com", Ct);
        var second = await fetcher.GetAsync("example.com", Ct);

        first.Should().NotBeNull();
        first!.Bytes.Should().Equal(1, 2, 3);
        first.ContentType.Should().Be("image/x-icon");
        second.Should().NotBeNull();
        handler.Uris.Should().ContainSingle().Which.Should().Be("https://example.com/favicon.ico");
    }

    [Fact]
    public async Task A_body_over_the_cap_is_refused_even_without_a_content_length()
    {
        var big = new byte[FaviconFetcher.MaxBytes + 1];
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new UnknownLengthContent(big, "image/png"),
        });
        using var fetcher = new FaviconFetcher(new StubFactory(handler));

        (await fetcher.GetAsync("big.example.com", Ct)).Should().BeNull();
    }

    [Fact]
    public async Task A_body_exactly_at_the_cap_is_kept()
    {
        var edge = new byte[FaviconFetcher.MaxBytes];
        var handler = new StubHandler(_ => Respond(HttpStatusCode.OK, "image/png", edge));
        using var fetcher = new FaviconFetcher(new StubFactory(handler));

        var icon = await fetcher.GetAsync("edge.example.com", Ct);

        icon.Should().NotBeNull();
        icon!.Bytes.Should().HaveCount(FaviconFetcher.MaxBytes);
    }

    [Theory]
    [InlineData("image/svg+xml")]
    [InlineData("text/html")]
    [InlineData("application/octet-stream")]
    public async Task A_type_off_the_allowlist_is_refused_and_the_miss_is_cached(string contentType)
    {
        var handler = new StubHandler(_ => Respond(HttpStatusCode.OK, contentType, new byte[] { 1 }));
        using var fetcher = new FaviconFetcher(new StubFactory(handler));

        (await fetcher.GetAsync("svg.example.com", Ct)).Should().BeNull();
        (await fetcher.GetAsync("svg.example.com", Ct)).Should().BeNull();

        handler.Uris.Should().HaveCount(1);
    }

    [Fact]
    public async Task A_redirect_is_a_miss_and_is_not_followed()
    {
        var handler = new StubHandler(_ =>
        {
            var r = Respond(HttpStatusCode.Found, "image/png", new byte[] { 1, 2, 3 });
            r.Headers.Location = new Uri("https://evil.example.net/favicon.ico");
            return r;
        });
        using var fetcher = new FaviconFetcher(new StubFactory(handler));

        (await fetcher.GetAsync("moved.example.com", Ct)).Should().BeNull();

        handler.Uris.Should().ContainSingle();
    }

    [Fact]
    public async Task A_connection_failure_is_a_miss()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("refused"));
        using var fetcher = new FaviconFetcher(new StubFactory(handler));

        (await fetcher.GetAsync("down.example.com", Ct)).Should().BeNull();
    }

    [Fact]
    public async Task Many_distinct_misses_stay_under_the_cache_limit()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        const long limit = 8 * 1024;
        using var fetcher = new FaviconFetcher(new StubFactory(handler), limit);

        for (var i = 0; i < 200; i++)
        {
            (await fetcher.GetAsync($"site{i}.example.com", Ct)).Should().BeNull();
        }

        handler.Uris.Should().HaveCount(200);
        fetcher.CachedEntries.Should().BeGreaterThan(0);
        fetcher.CachedEntries.Should().BeLessThan((int)(limit / 256) + 1);
        fetcher.CachedEntries.Should().BeLessThan(200);
    }

    [Fact]
    public async Task A_body_over_the_cap_is_not_read_past_the_cap()
    {
        var counting = new CountingStream(10 * 1024 * 1024);
        var handler = new StubHandler(_ =>
        {
            var content = new StreamContent(counting);
            content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        using var fetcher = new FaviconFetcher(new StubFactory(handler));

        (await fetcher.GetAsync("huge.example.com", Ct)).Should().BeNull();

        counting.BytesRead.Should().BeGreaterThan(0);
        counting.BytesRead.Should().BeLessThanOrEqualTo(FaviconFetcher.MaxBytes + 1);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task A_server_error_is_retried_after_five_minutes_not_a_day(HttpStatusCode status)
    {
        var clock = new Clock();
        var handler = new StubHandler(_ => new HttpResponseMessage(status));
        using var fetcher = new FaviconFetcher(new StubFactory(handler), time: clock);

        await fetcher.GetAsync("flaky.example.com", Ct);
        clock.Advance(TimeSpan.FromMinutes(4));
        await fetcher.GetAsync("flaky.example.com", Ct);
        handler.Uris.Should().HaveCount(1);

        clock.Advance(TimeSpan.FromMinutes(2));
        await fetcher.GetAsync("flaky.example.com", Ct);
        handler.Uris.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_connection_failure_is_retried_after_five_minutes()
    {
        var clock = new Clock();
        var handler = new StubHandler(_ => throw new HttpRequestException("refused"));
        using var fetcher = new FaviconFetcher(new StubFactory(handler), time: clock);

        await fetcher.GetAsync("down.example.com", Ct);
        clock.Advance(TimeSpan.FromMinutes(6));
        await fetcher.GetAsync("down.example.com", Ct);

        handler.Uris.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_404_is_kept_for_a_day()
    {
        var clock = new Clock();
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var fetcher = new FaviconFetcher(new StubFactory(handler), time: clock);

        await fetcher.GetAsync("none.example.com", Ct);
        clock.Advance(TimeSpan.FromHours(23));
        await fetcher.GetAsync("none.example.com", Ct);
        handler.Uris.Should().HaveCount(1);

        clock.Advance(TimeSpan.FromHours(2));
        await fetcher.GetAsync("none.example.com", Ct);
        handler.Uris.Should().HaveCount(2);
    }

    [Fact]
    public async Task Concurrent_cold_requests_for_one_host_share_one_fetch()
    {
        var release = new TaskCompletionSource();
        var handler = new StubHandler(_ => Respond(HttpStatusCode.OK, "image/png", new byte[] { 9 }))
        {
            Gate = release.Task,
        };
        using var fetcher = new FaviconFetcher(new StubFactory(handler));

        var calls = Enumerable.Range(0, 10).Select(_ => fetcher.GetAsync("busy.example.com", Ct)).ToList();
        release.SetResult();
        var icons = await Task.WhenAll(calls);

        icons.Should().HaveCount(10);
        icons.Should().OnlyContain(i => i != null);
        handler.Uris.Should().ContainSingle();
    }

    private static HttpResponseMessage Respond(HttpStatusCode status, string contentType, byte[] body)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return new HttpResponseMessage(status) { Content = content };
    }

    private sealed class UnknownLengthContent : HttpContent
    {
        private readonly byte[] _body;

        public UnknownLengthContent(byte[] body, string contentType)
        {
            _body = body;
            Headers.ContentType = new MediaTypeHeaderValue(contentType);
        }

        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            stream.WriteAsync(_body).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private readonly object _lock = new();
        private readonly List<string> _uris = new();

        public List<string> Uris
        {
            get
            {
                lock (_lock)
                {
                    return _uris.ToList();
                }
            }
        }

        public Task? Gate { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (_lock)
            {
                _uris.Add(request.RequestUri!.ToString());
            }

            if (Gate is not null)
            {
                await Gate;
            }

            return respond(request);
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            name.Should().Be("ExternalApi");
            return new HttpClient(handler, disposeHandler: false);
        }
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class CountingStream(long length) : Stream
    {
        private long _position;

        public long BytesRead { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Take(count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var n = Take(buffer.Length);
            buffer.Span[..n].Clear();
            return ValueTask.FromResult(n);
        }

        private int Take(int count)
        {
            var n = (int)Math.Min(count, length - _position);
            _position += n;
            BytesRead += n;
            return n;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
