using System.Net;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// Semantic search is anonymous and spends an embedding call per request, so it has a per-address
/// limit of its own, tunable under Modules:AI.
/// </summary>
[Collection("Integration1")]
public class AiSemanticRateLimitTests(IntegrationTestFixture factory)
{
    [Fact]
    public async Task Semantic_search_is_limited_per_address_and_the_limit_can_be_tuned()
    {
        var host = factory.WithSettings(new Dictionary<string, string?>
        {
            ["Modules:AI:SemanticSearchPermitLimit"] = "3",
            ["Modules:AI:SemanticSearchWindowSeconds"] = "60",
        });
        HttpClient Visitor(string ip)
        {
            var client = host.CreateClient();
            client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, ip);
            return client;
        }

        var visitor = Visitor("2001:db8:5401:1::1");
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
        {
            using var response = await visitor.GetAsync(
                "/api/public/no-such-type/semantic?q=anything", TestContext.Current.CancellationToken);
            codes.Add(response.StatusCode);
        }

        codes.Should().HaveCount(4);
        codes.Take(3).Should().OnlyContain(c => c == HttpStatusCode.NotFound, "the first three are within the limit");
        codes[3].Should().Be(HttpStatusCode.TooManyRequests);

        using var other = await Visitor("2001:db8:5401:2::1").GetAsync(
            "/api/public/no-such-type/semantic?q=anything", TestContext.Current.CancellationToken);
        other.StatusCode.Should().Be(HttpStatusCode.NotFound, "another address has its own bucket");
    }

    private const string Path = "/api/public/no-such-type/semantic?q=anything";
    private const string RendererKey = "renderer-key-for-semantic-0123456789abcdef";

    private static HttpClient Visitor(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> host, string ip, string? rendererKey = null, string? visitorIp = null)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, ip);
        if (rendererKey is not null) client.DefaultRequestHeaders.Add("X-Barako-Renderer-Key", rendererKey);
        if (visitorIp is not null) client.DefaultRequestHeaders.Add("X-Barako-Visitor-IP", visitorIp);
        return client;
    }

    private static async Task<List<HttpStatusCode>> Get(HttpClient client, int times)
    {
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < times; i++)
        {
            using var response = await client.GetAsync(Path, TestContext.Current.CancellationToken);
            codes.Add(response.StatusCode);
        }
        return codes;
    }

    [Fact]
    public async Task Two_addresses_in_one_ipv6_prefix_share_a_bucket()
    {
        var host = factory.WithSettings(new Dictionary<string, string?>
        {
            ["Modules:AI:SemanticSearchPermitLimit"] = "3",
            ["Modules:AI:SemanticSearchWindowSeconds"] = "60",
        });
        var one = Visitor(host, "2001:db8:5402:7::1");
        var two = Visitor(host, "2001:db8:5402:7::2");

        var first = await Get(one, 2);
        var second = await Get(two, 1);
        var third = await Get(one, 1);

        first.Concat(second).Should().HaveCount(3).And.OnlyContain(c => c == HttpStatusCode.NotFound);
        third.Should().ContainSingle().Which.Should().Be(HttpStatusCode.TooManyRequests, "both addresses are one subscriber's /64");
    }

    [Fact]
    public async Task A_renderer_is_counted_per_visitor_it_names_and_not_as_one_address()
    {
        var host = factory.WithSettings(new Dictionary<string, string?>
        {
            ["Modules:AI:SemanticSearchPermitLimit"] = "3",
            ["Modules:AI:SemanticSearchWindowSeconds"] = "60",
            ["RateLimiting:Renderer:Key"] = RendererKey,
        });
        const string renderer = "2001:db8:5403:1::1";

        var visitorA = await Get(Visitor(host, renderer, RendererKey, "198.51.100.10"), 4);
        var visitorB = await Get(Visitor(host, renderer, RendererKey, "198.51.100.11"), 3);
        var unnamed = await Get(Visitor(host, renderer, RendererKey), 5);

        visitorA.Take(3).Should().HaveCount(3).And.OnlyContain(c => c == HttpStatusCode.NotFound);
        visitorA[3].Should().Be(HttpStatusCode.TooManyRequests, "a named visitor has the limit to itself");
        visitorB.Should().HaveCount(3).And.OnlyContain(c => c == HttpStatusCode.NotFound,
            "another visitor behind the same renderer has its own bucket");
        unnamed.Should().HaveCount(5).And.OnlyContain(c => c == HttpStatusCode.NotFound,
            "a renderer naming no visitor stays on the renderer bucket alone");

        var wrongKey = await Get(Visitor(host, "2001:db8:5403:2::1", RendererKey + "x", "198.51.100.12"), 4);
        wrongKey.Last().Should().Be(HttpStatusCode.TooManyRequests, "a wrong key is an ordinary client counted by its address");
    }
}
