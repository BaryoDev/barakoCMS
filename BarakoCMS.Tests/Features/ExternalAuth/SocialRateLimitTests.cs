using System.Net;
using System.Net.Http.Json;
using BarakoCMS.ExternalAuth;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace BarakoCMS.Tests.Features.ExternalAuth;

/// <summary>The limit on the code exchange and the anonymous deletion routes reads its numbers from config (#1270).</summary>
[Collection("Integration1")]
public class SocialRateLimitTests
{
    private static readonly Lock Gate = new();
    private static WebApplicationFactory<Program>? _host;
    private static int _ipCounter = 100;

    public SocialRateLimitTests(IntegrationTestFixture fixture)
    {
        lock (Gate)
        {
            _host ??= fixture.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    { "Social:RateLimit:PermitLimit", "2" },
                    { "Social:RateLimit:WindowSeconds", "60" },
                    { "Social:RateLimit:ExchangePermitLimit", "3" },
                    { "Social:RateLimit:ExchangeWindowSeconds", "60" },
                    { "Facebook:AppSecret", SignedRequests.Secret },
                })));
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IConfiguration Config(params (string Key, string Value)[] values) => new ConfigurationBuilder()
        .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value))
        .Build();

    private static HttpClient Client(string? ip = null)
    {
        var client = _host!.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, ip ?? $"203.0.113.{Interlocked.Increment(ref _ipCounter) % 250 + 1}");
        return client;
    }

    [Fact]
    public void The_limit_defaults_to_ten_per_sixty_seconds()
    {
        SocialRateLimit.PermitLimit(Config()).Should().Be(10);
        SocialRateLimit.Window(Config()).Should().Be(TimeSpan.FromSeconds(60));
        SocialRateLimit.Section.Should().Be("Social:RateLimit");
    }

    [Fact]
    public void The_exchange_limit_defaults_to_three_hundred_per_sixty_seconds_and_reads_its_own_keys()
    {
        SocialRateLimit.ExchangePermitLimit(Config()).Should().Be(300);
        SocialRateLimit.ExchangeWindow(Config()).Should().Be(TimeSpan.FromSeconds(60));

        var config = Config(("Social:RateLimit:ExchangePermitLimit", "40"), ("Social:RateLimit:ExchangeWindowSeconds", "7"),
            ("Social:RateLimit:PermitLimit", "2"), ("Social:RateLimit:WindowSeconds", "9"));
        SocialRateLimit.ExchangePermitLimit(config).Should().Be(40);
        SocialRateLimit.ExchangeWindow(config).Should().Be(TimeSpan.FromSeconds(7));
        SocialRateLimit.PermitLimit(config).Should().Be(2, "the deletion routes keep their own setting");
        SocialRateLimit.ExchangePermitLimit(Config(("Social:RateLimit:ExchangePermitLimit", "0"))).Should().Be(300);
    }

    [Fact]
    public void The_limit_reads_its_numbers_from_config()
    {
        var config = Config(("Social:RateLimit:PermitLimit", "3"), ("Social:RateLimit:WindowSeconds", "5"));

        SocialRateLimit.PermitLimit(config).Should().Be(3);
        SocialRateLimit.Window(config).Should().Be(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-4")]
    [InlineData("many")]
    public void A_value_that_is_not_a_positive_number_falls_back_to_the_default(string value)
    {
        var config = Config(("Social:RateLimit:PermitLimit", value), ("Social:RateLimit:WindowSeconds", value));

        SocialRateLimit.PermitLimit(config).Should().Be(10);
        SocialRateLimit.Window(config).Should().Be(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task The_exchange_answers_429_after_the_configured_number_of_requests_from_one_address()
    {
        var client = Client();
        var other = Client();

        for (var i = 0; i < 3; i++)
        {
            (await client.PostAsJsonAsync("/api/auth/social/exchange", new { code = "nope", device = "d" }, Ct))
                .StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests);
        }

        (await client.PostAsJsonAsync("/api/auth/social/exchange", new { code = "nope", device = "d" }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await other.PostAsJsonAsync("/api/auth/social/exchange", new { code = "nope", device = "d" }, Ct))
            .StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests, "another address has its own bucket");
    }

    [Fact]
    public async Task The_deletion_routes_are_limited_too()
    {
        var code = new string('a', 64);
        var post = Client();
        var get = Client();
        var choice = Client();

        for (var i = 0; i < 2; i++)
        {
            await post.PostAsync("/api/auth/facebook/data-deletion", new FormUrlEncodedContent(
                [new KeyValuePair<string, string>("signed_request", "bad")]), Ct);
            await get.GetAsync($"/api/auth/facebook/data-deletion/status/{code}", Ct);
            await choice.PostAsJsonAsync($"/api/auth/facebook/data-deletion/status/{code}", new { choice = "keep_account" }, Ct);
        }

        (await post.PostAsync("/api/auth/facebook/data-deletion", new FormUrlEncodedContent(
            [new KeyValuePair<string, string>("signed_request", "bad")]), Ct)).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await get.GetAsync($"/api/auth/facebook/data-deletion/status/{code}", Ct)).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await choice.PostAsJsonAsync($"/api/auth/facebook/data-deletion/status/{code}", new { choice = "keep_account" }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    private static Task<HttpResponseMessage> Exchange(HttpClient client) =>
        client.PostAsJsonAsync("/api/auth/social/exchange", new { code = "nope" }, Ct);

    private static Task<HttpResponseMessage> DeletionStatus(HttpClient client) =>
        client.GetAsync($"/api/auth/facebook/data-deletion/status/{new string('a', 64)}", Ct);

    [Fact]
    public async Task The_exchange_and_the_deletion_routes_do_not_share_a_bucket()
    {
        var spent = Client();
        for (var i = 0; i < 2; i++) await DeletionStatus(spent);
        (await DeletionStatus(spent)).StatusCode.Should().Be(HttpStatusCode.TooManyRequests, "the deletion bucket of two is spent");

        // Three exchanges from the same address still pass: the exchange has a bucket of three.
        for (var i = 0; i < 3; i++)
        {
            (await Exchange(spent)).StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests);
        }

        (await Exchange(spent)).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

        // And the other way round: a spent exchange bucket leaves the deletion routes alone.
        var exchanged = Client();
        for (var i = 0; i < 4; i++) await Exchange(exchanged);
        (await Exchange(exchanged)).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await DeletionStatus(exchanged)).StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests);
    }
}
