using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// Proves the resolve endpoint's <c>CacheOutput</c> policy (#545) is backed by real output-cache
/// middleware, not metadata FastEndpoints reads and nobody enforces.
/// </summary>
/// <remarks>
/// Reuses <see cref="CapturingMartenLogger"/> from <c>SlugLookupPushdownTests</c>: it records every
/// statement Marten sends to Postgres, so a passing test proves a second identical request never
/// asked the database, rather than merely running fast (a timing assertion would be flaky and would
/// not distinguish a real cache from a database that just happens to answer quickly).
/// </remarks>
[Collection("Sequential")]
public class RedirectOutputCacheTests
{
    private readonly IntegrationTestFixture _factory;
    private readonly CapturingMartenLogger _logger = new();
    private readonly HttpClient _anonymousClient;

    public RedirectOutputCacheTests(IntegrationTestFixture factory)
    {
        _factory = factory;

        // Do NOT dispose the derived factory below: see WithSetting's own doc comment on this same
        // fixture. #209 recorded five unrelated PreviewTests failing from exactly that, so it is not
        // this test's call to make differently.
        var derived = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.ConfigureMarten(options => options.Logger(_logger))));

        _anonymousClient = derived.CreateClient();
    }

    private static string Unique() => "/old-" + Guid.NewGuid().ToString("N")[..10];

    [Fact]
    public async Task A_second_identical_resolve_does_not_reach_the_database()
    {
        var (token, _) = await TestHelpers.CreateAdminUserAsync(_factory);
        var from = Unique();

        using var authenticated = _factory.CreateClient();
        authenticated.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var save = await authenticated.PostAsJsonAsync("/api/redirects",
            new { fromPath = from, toPath = "/new-home", permanent = true },
            TestContext.Current.CancellationToken);
        save.IsSuccessStatusCode.Should().BeTrue(
            await save.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        var query = $"/api/public/redirects/resolve?path={Uri.EscapeDataString(from)}";

        var first = await _anonymousClient.GetAsync(query, TestContext.Current.CancellationToken);
        var firstBody = await first.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        first.IsSuccessStatusCode.Should().BeTrue(firstBody);

        _logger.Commands.Should().Contain(c => c.Parameters.Contains(from),
            "the first request has to actually look the redirect up in Postgres, or the assertion "
          + "below would pass without the cache doing anything");

        // Only the second request is under test from here.
        _logger.Commands.Clear();

        var second = await _anonymousClient.GetAsync(query, TestContext.Current.CancellationToken);
        var secondBody = await second.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        second.IsSuccessStatusCode.Should().BeTrue(secondBody);

        secondBody.Should().Be(firstBody, "a cached answer is the same answer, not merely a fast one");

        _logger.Commands.Should().NotContain(c => c.Parameters.Contains(from),
            "a second identical request should be served from the output cache, not ask Postgres again");
    }
}
