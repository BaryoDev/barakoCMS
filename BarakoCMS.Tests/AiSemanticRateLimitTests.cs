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
}
