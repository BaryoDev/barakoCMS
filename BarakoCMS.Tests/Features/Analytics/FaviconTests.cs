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
            var r = new HttpResponseMessage(HttpStatusCode.Found);
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
        public List<string> Uris { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Uris.Add(request.RequestUri!.ToString());
            return Task.FromResult(respond(request));
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
}
