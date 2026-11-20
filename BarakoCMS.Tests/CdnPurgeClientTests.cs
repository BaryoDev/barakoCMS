using System.Net;
using System.Text.Json;
using barakoCMS.Infrastructure.Caching.Purge;
using barakoCMS.Infrastructure.Http;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// The purge client talks to Cloudflare and Fastly through a stand-in handler, so no test reaches a
/// real CDN (#561).
/// </summary>
public class CdnPurgeClientTests
{
    private const string Token = "tok_PLACEHOLDER_not_a_real_token";
    private const string Zone = "zone123placeholder";
    private const string Service = "service123placeholder";

    private sealed record Sent(HttpMethod Method, Uri Uri, string? Authorization, string? FastlyKey, string Body);

    private sealed class StubHandler(Func<int, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<Sent> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
            request.Headers.TryGetValues("Fastly-Key", out var key);
            Calls.Add(new Sent(request.Method, request.RequestUri!, request.Headers.Authorization?.ToString(), key?.SingleOrDefault(), body));
            return answer(Calls.Count);
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Lines.Add(formatter(state, exception));
            if (exception is not null)
            {
                Lines.Add(exception.ToString());
            }
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body = "{}") =>
        new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private static CloudflarePurger Cloudflare(StubHandler handler, ILogger<CloudflarePurger>? logger = null) =>
        new(new HttpClient(handler),
            new CdnPurgeTarget(CdnPurgeProvider.Cloudflare, Zone, Token, CloudflarePurger.DefaultBaseUrl),
            logger ?? new CapturingLogger<CloudflarePurger>());

    private static FastlyPurger Fastly(StubHandler handler, ILogger<FastlyPurger>? logger = null) =>
        new(new HttpClient(handler),
            new CdnPurgeTarget(CdnPurgeProvider.Fastly, Service, Token, FastlyPurger.DefaultBaseUrl),
            logger ?? new CapturingLogger<FastlyPurger>());

    private static List<string> Tags(int count) => Enumerable.Range(0, count).Select(i => $"t:acme:entry:{i}").ToList();

    private static string[] BodyList(Sent call, string property)
    {
        using var doc = JsonDocument.Parse(call.Body);
        return doc.RootElement.GetProperty(property).EnumerateArray().Select(e => e.GetString()!).ToArray();
    }

    [Fact]
    public async Task Cloudflare_sends_tags_as_json_with_a_bearer_token_to_the_zone_url()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK));

        var result = await Cloudflare(handler).PurgeTagsAsync(["t:acme:entry:1", "t:acme:sitemap"], TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue();
        result.Status.Should().Be(200);
        handler.Calls.Should().ContainSingle();
        var call = handler.Calls[0];
        call.Method.Should().Be(HttpMethod.Post);
        call.Uri.ToString().Should().Be($"https://api.cloudflare.com/client/v4/zones/{Zone}/purge_cache");
        call.Authorization.Should().Be($"Bearer {Token}");
        BodyList(call, "tags").Should().Equal("t:acme:entry:1", "t:acme:sitemap");
    }

    [Fact]
    public async Task Cloudflare_splits_more_than_100_tags_into_calls_of_100_and_stops_at_the_first_failure()
    {
        CloudflarePurger.MaxTagsPerCall.Should().Be(100);

        var all = new StubHandler(_ => Json(HttpStatusCode.OK));
        (await Cloudflare(all).PurgeTagsAsync(Tags(250), TestContext.Current.CancellationToken)).Succeeded.Should().BeTrue();
        all.Calls.Select(c => BodyList(c, "tags").Length).Should().Equal(100, 100, 50);

        var failing = new StubHandler(n => n == 2 ? Json(HttpStatusCode.InternalServerError) : Json(HttpStatusCode.OK));
        var result = await Cloudflare(failing).PurgeTagsAsync(Tags(250), TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Status.Should().Be(500);
        failing.Calls.Should().HaveCount(2, "the third batch is not sent once the second failed");
    }

    [Fact]
    public async Task Fastly_sends_surrogate_keys_with_the_Fastly_Key_header_to_the_service_url()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK));

        var result = await Fastly(handler).PurgeTagsAsync(["t:acme:entry:1", "t:acme:type:post"], TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue();
        handler.Calls.Should().ContainSingle();
        var call = handler.Calls[0];
        call.Method.Should().Be(HttpMethod.Post);
        call.Uri.ToString().Should().Be($"https://api.fastly.com/service/{Service}/purge");
        call.FastlyKey.Should().Be(Token);
        call.Authorization.Should().BeNull();
        BodyList(call, "surrogate_keys").Should().Equal("t:acme:entry:1", "t:acme:type:post");
    }

    [Fact]
    public async Task Fastly_splits_more_than_256_keys_into_calls()
    {
        FastlyPurger.MaxKeysPerCall.Should().Be(256);
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK));

        var result = await Fastly(handler).PurgeTagsAsync(Tags(600), TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue();
        handler.Calls.Select(c => BodyList(c, "surrogate_keys").Length).Should().Equal(256, 256, 88);
    }

    [Fact]
    public async Task A_429_is_retryable_and_carries_Retry_After()
    {
        var handler = new StubHandler(_ =>
        {
            var response = Json((HttpStatusCode)429);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
            return response;
        });

        var result = await Cloudflare(handler).PurgeTagsAsync(["t:acme:entry:1"], TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Status.Should().Be(429);
        result.Retryable.Should().BeTrue();
        result.RetryAfter.Should().Be(TimeSpan.FromSeconds(120));
    }

    [Theory]
    [InlineData(500, true)]
    [InlineData(502, true)]
    [InlineData(503, true)]
    [InlineData(400, false)]
    [InlineData(401, false)]
    [InlineData(403, false)]
    [InlineData(404, false)]
    public async Task A_5xx_and_a_network_error_are_retryable_and_a_401_403_and_404_are_not(int status, bool retryable)
    {
        var cloudflare = new StubHandler(_ => Json((HttpStatusCode)status));
        var fastly = new StubHandler(_ => Json((HttpStatusCode)status));

        var a = await Cloudflare(cloudflare).PurgeTagsAsync(["t:acme:entry:1"], TestContext.Current.CancellationToken);
        var b = await Fastly(fastly).PurgeTagsAsync(["t:acme:entry:1"], TestContext.Current.CancellationToken);

        a.Succeeded.Should().BeFalse();
        a.Status.Should().Be(status);
        a.Retryable.Should().Be(retryable);
        b.Succeeded.Should().BeFalse();
        b.Status.Should().Be(status);
        b.Retryable.Should().Be(retryable);
    }

    [Fact]
    public async Task A_network_error_is_retryable_and_has_no_status()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("connection refused"));

        var result = await Cloudflare(handler).PurgeTagsAsync(["t:acme:entry:1"], TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Status.Should().BeNull();
        result.Retryable.Should().BeTrue();
        result.Error.Should().Contain("connection refused");
    }

    [Fact]
    public async Task The_token_never_appears_in_a_result_error_or_a_log_line_even_when_the_provider_echoes_it()
    {
        var echoed = $"{{\"errors\":[{{\"message\":\"bad credentials {Token}\"}}],\"msg\":\"bad credentials {Token}\",\"detail\":\"key {Token}\"}}";

        var cfLog = new CapturingLogger<CloudflarePurger>();
        var cf = await Cloudflare(new StubHandler(_ => Json(HttpStatusCode.Forbidden, echoed)), cfLog)
            .PurgeTagsAsync(["t:acme:entry:1"], TestContext.Current.CancellationToken);
        var fastlyLog = new CapturingLogger<FastlyPurger>();
        var fastly = await Fastly(new StubHandler(_ => Json(HttpStatusCode.Forbidden, echoed)), fastlyLog)
            .PurgeTagsAsync(["t:acme:entry:1"], TestContext.Current.CancellationToken);
        var netLog = new CapturingLogger<CloudflarePurger>();
        var net = await Cloudflare(new StubHandler(_ => throw new HttpRequestException($"proxy said {Token}")), netLog)
            .PurgeTagsAsync(["t:acme:entry:1"], TestContext.Current.CancellationToken);

        foreach (var result in new[] { cf, fastly, net })
        {
            result.Error.Should().NotBeNullOrEmpty();
            result.Error.Should().NotContain(Token);
            result.ToString().Should().NotContain(Token);
        }
        cf.Error.Should().Contain("bad credentials", "the provider's reason is still useful once the token is removed");
        cfLog.Lines.Should().NotBeEmpty();
        fastlyLog.Lines.Should().NotBeEmpty();
        netLog.Lines.Should().NotBeEmpty();
        cfLog.Lines.Concat(fastlyLog.Lines).Concat(netLog.Lines).Should().OnlyContain(line => !line.Contains(Token));
    }

    [Fact]
    public async Task An_empty_tag_list_sends_nothing()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK));

        var cf = await Cloudflare(handler).PurgeTagsAsync([], TestContext.Current.CancellationToken);
        var fastly = await Fastly(handler).PurgeTagsAsync([], TestContext.Current.CancellationToken);

        cf.Succeeded.Should().BeTrue();
        fastly.Succeeded.Should().BeTrue();
        handler.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData("cloudflare")]
    [InlineData("fastly")]
    public async Task A_timeout_is_retryable_and_does_not_throw(string provider)
    {
        var handler = new StubHandler(_ => throw new TimeoutException("The attempt timed out."));

        var result = provider == "cloudflare"
            ? await Cloudflare(handler).PurgeTagsAsync(["t:acme:entry:1"], TestContext.Current.CancellationToken)
            : await Fastly(handler).PurgeTagsAsync(["t:acme:entry:1"], TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Retryable.Should().BeTrue();
        result.Status.Should().BeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_blocked_address_is_not_retryable(bool wrapped)
    {
        var handler = new StubHandler(_ => throw (wrapped
            ? new HttpRequestException("send failed", new BlockedAddressException("blocked"))
            : new BlockedAddressException("blocked")));

        var cf = await Cloudflare(handler).PurgeTagsAsync(["t:acme:entry:1"], TestContext.Current.CancellationToken);
        var fastly = await Fastly(handler).PurgeTagsAsync(["t:acme:entry:1"], TestContext.Current.CancellationToken);

        cf.Succeeded.Should().BeFalse();
        cf.Retryable.Should().BeFalse();
        fastly.Succeeded.Should().BeFalse();
        fastly.Retryable.Should().BeFalse();
    }

    [Fact]
    public async Task A_provider_error_with_a_line_break_is_logged_on_one_line()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.BadRequest, """{"errors":[{"message":"a\nb\r\nc"}]}"""));
        var log = new CapturingLogger<CloudflarePurger>();

        var result = await Cloudflare(handler, log).PurgeTagsAsync(["t:acme:entry:1"], TestContext.Current.CancellationToken);

        result.Error.Should().Contain("a b").And.NotContainAny("\r", "\n");
        log.Lines.Should().NotBeEmpty();
        log.Lines.Should().OnlyContain(line => !line.Contains('\r') && !line.Contains('\n'));
    }

    [Fact]
    public async Task A_long_error_body_is_cut()
    {
        var huge = new string('x', 100_000);
        var handler = new StubHandler(_ => Json(HttpStatusCode.BadRequest, $$"""{"errors":[{"message":"{{huge}}"}]}"""));

        var result = await Cloudflare(handler).PurgeTagsAsync(["t:acme:entry:1"], TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().NotBeNull();
        result.Error!.Length.Should().BeLessThanOrEqualTo(500);
    }

    [Fact]
    public async Task Tags_with_percent_and_colon_are_sent_unchanged_in_the_body()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK));
        string[] tags = ["t:acme:type:a%20b", "t:acme:entry:9f1c"];

        await Cloudflare(handler).PurgeTagsAsync(tags, TestContext.Current.CancellationToken);
        await Fastly(handler).PurgeTagsAsync(tags, TestContext.Current.CancellationToken);

        handler.Calls.Should().HaveCount(2);
        BodyList(handler.Calls[0], "tags").Should().Equal(tags);
        BodyList(handler.Calls[1], "surrogate_keys").Should().Equal(tags);
    }
}
