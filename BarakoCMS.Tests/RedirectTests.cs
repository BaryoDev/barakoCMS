using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Features.Redirects;
using barakoCMS.Models;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// Redirects, and mostly the rules that decide whether they are any good.
/// </summary>
/// <remarks>
/// The loop cases carry the weight. A loop found at request time is found on the 404 path, which is
/// when a site is already having a bad day, by somebody who cannot fix it, and it presents as "too
/// many redirects" long after anybody remembers writing the rule. Refusing at save time is the whole
/// feature.
/// </remarks>
public class RedirectRulesTests
{
    private static Dictionary<string, string> Map(params (string From, string To)[] rules) =>
        rules.ToDictionary(r => r.From, r => r.To, StringComparer.Ordinal);

    [Fact]
    public void A_path_that_redirects_to_itself_is_refused()
    {
        RedirectRules.Refuse("/about", "/about", Map()).Should().Contain("redirects to itself");
    }

    [Fact]
    public void A_two_step_loop_is_refused()
    {
        // /a to /b already exists. Adding /b to /a closes the circle, and neither rule looks wrong
        // on its own, which is why this has to be checked against what is already stored.
        var existing = Map(("/a", "/b"));

        RedirectRules.Refuse("/b", "/a", existing).Should().Contain("loop");
    }

    [Fact]
    public void A_longer_loop_is_refused()
    {
        var existing = Map(("/a", "/b"), ("/b", "/c"), ("/c", "/d"));

        RedirectRules.Refuse("/d", "/a", existing).Should().Contain("loop");
    }

    [Fact]
    public void A_chain_that_ends_somewhere_real_is_allowed()
    {
        // The pairing, and the one that matters most: a rule refusing every chain would pass all
        // three tests above and make the feature useless. A chain is normal, a circle is not.
        var existing = Map(("/b", "/c"), ("/c", "/final"));

        RedirectRules.Refuse("/a", "/b", existing).Should().BeNull();
    }

    [Fact]
    public void A_chain_longer_than_the_cap_is_refused_even_though_it_ends()
    {
        var existing = Map(Enumerable.Range(0, RedirectRules.MaxChain + 2)
            .Select(i => ($"/p{i}", $"/p{i + 1}"))
            .ToArray());

        var refusal = RedirectRules.Refuse("/start", "/p0", existing);

        refusal.Should().NotBeNull();
        refusal.Should().Contain("chain longer than");
        refusal.Should().NotContain("loop", "this one terminates, so calling it a loop would mislead");
    }

    [Theory]
    [InlineData("about", "/about")]
    [InlineData("/about/", "/about")]
    [InlineData("//about", "/about")]
    [InlineData("/about?utm_source=x", "/about")]
    [InlineData("/about#top", "/about")]
    [InlineData("  /about  ", "/about")]
    [InlineData("", "/")]
    [InlineData("/", "/")]
    public void A_path_is_stored_in_one_form(string input, string expected)
    {
        // Otherwise "/about", "about" and "/about/" are three rules that look like one, and the
        // unique index does not catch it because they are genuinely different strings.
        UrlRedirect.Normalize(input).Should().Be(expected);
    }

    [Fact]
    public void Case_is_preserved_because_paths_are_case_sensitive()
    {
        // Lowering would make a rule match a path nobody wrote it for. Lookup compares exactly, so
        // what is stored has to be what was meant.
        UrlRedirect.Normalize("/About-Us").Should().Be("/About-Us");
    }
}

/// <summary>The redirect endpoints end to end, including the anonymous resolve.</summary>
[Collection("Sequential")]
public class RedirectEndpointTests
{
    private readonly IntegrationTestFixture _factory;
    private readonly HttpClient _client;

    public RedirectEndpointTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private async Task AuthenticateAsync()
    {
        var (token, _) = await TestHelpers.CreateAdminUserAsync(_factory);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private static string Unique() => "/old-" + Guid.NewGuid().ToString("N")[..10];

    private async Task<HttpResponseMessage> SaveAsync(string from, string to, bool permanent = false) =>
        await _client.PostAsJsonAsync("/api/redirects", new { fromPath = from, toPath = to, permanent },
            TestContext.Current.CancellationToken);

    [Fact]
    public async Task A_saved_redirect_resolves_anonymously_with_the_right_status()
    {
        await AuthenticateAsync();
        var from = Unique();

        (await SaveAsync(from, "/new-home", permanent: true)).IsSuccessStatusCode.Should().BeTrue();

        // A separate client with no token, because the whole point is that a frontend rendering for
        // a visitor can ask.
        using var anonymous = _factory.CreateClient();
        var response = await anonymous.GetAsync(
            $"/api/public/redirects/resolve?path={Uri.EscapeDataString(from)}",
            TestContext.Current.CancellationToken);

        response.IsSuccessStatusCode.Should().BeTrue(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        var body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;

        body.GetProperty("toPath").GetString().Should().Be("/new-home");
        body.GetProperty("status").GetInt32().Should().Be(301, "permanent was set");
    }

    [Fact]
    public async Task A_temporary_redirect_resolves_as_302()
    {
        // Paired with the test above. Without it, a resolve that always answered 301 would pass, and
        // 301 is the one a browser caches forever.
        await AuthenticateAsync();
        var from = Unique();

        (await SaveAsync(from, "/somewhere")).IsSuccessStatusCode.Should().BeTrue();

        using var anonymous = _factory.CreateClient();
        var body = JsonDocument.Parse(await (await anonymous.GetAsync(
                $"/api/public/redirects/resolve?path={Uri.EscapeDataString(from)}",
                TestContext.Current.CancellationToken))
            .Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;

        body.GetProperty("status").GetInt32().Should().Be(302,
            "permanent defaults to false, because a 301 entered by mistake cannot be taken back");
    }

    [Fact]
    public async Task A_path_nobody_moved_answers_404_rather_than_an_empty_success()
    {
        using var anonymous = _factory.CreateClient();

        var response = await anonymous.GetAsync(
            $"/api/public/redirects/resolve?path=/never-existed-{Guid.NewGuid():N}",
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "an empty 200 would make no redirect and a redirect to nowhere the same answer");
    }

    [Fact]
    public async Task A_loop_is_refused_at_save_time()
    {
        await AuthenticateAsync();
        var a = Unique();
        var b = Unique();

        (await SaveAsync(a, b)).IsSuccessStatusCode.Should().BeTrue();

        var closing = await SaveAsync(b, a);

        closing.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await closing.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should().Contain("loop");
    }

    [Fact]
    public async Task An_import_rejects_the_bad_lines_and_keeps_the_good_ones()
    {
        await AuthenticateAsync();
        var good = Unique();
        var alsoGood = Unique();
        var selfLoop = Unique();

        var csv = string.Join("\n",
            "from,to,permanent",
            $"{good},/landing,true",
            $"{selfLoop},{selfLoop}",
            $"{alsoGood},/landing",
            "no-comma-on-this-line");

        var response = await _client.PostAsJsonAsync("/api/redirects/import",
            new { csv, dryRun = false }, TestContext.Current.CancellationToken);

        response.IsSuccessStatusCode.Should().BeTrue(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        var report = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;

        report.GetProperty("created").GetInt32().Should().Be(2, "the header is skipped, two lines are good");

        var rejected = report.GetProperty("rejected").EnumerateArray().Select(e => e.GetString()!).ToList();
        rejected.Should().HaveCount(2);
        rejected.Should().Contain(r => r.Contains("redirects to itself"));
        rejected.Should().Contain(r => r.Contains("Line 5"));

        // And the good ones actually landed, which the counts alone do not prove.
        using var scope = _factory.Services.CreateScope();
        var stored = await scope.ServiceProvider.GetRequiredService<IQuerySession>()
            .Query<UrlRedirect>().Where(r => r.FromPath == good).ToListAsync(TestContext.Current.CancellationToken);

        stored.Should().ContainSingle();
        stored[0].Permanent.Should().BeTrue("the third column said so");
    }

    [Fact]
    public async Task An_import_catches_a_loop_that_no_single_line_creates()
    {
        // The case checking each line against the database alone would miss: line 2 is fine against
        // what is stored, and closes a circle with line 1 of the same upload.
        await AuthenticateAsync();
        var a = Unique();
        var b = Unique();

        var csv = string.Join("\n", $"{a},{b}", $"{b},{a}");

        var response = await _client.PostAsJsonAsync("/api/redirects/import",
            new { csv, dryRun = true }, TestContext.Current.CancellationToken);

        var report = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;

        report.GetProperty("created").GetInt32().Should().Be(1, "the first line is fine on its own");
        report.GetProperty("rejected").EnumerateArray().Should().ContainSingle()
            .Which.GetString().Should().Contain("loop");
    }

    [Fact]
    public async Task A_dry_run_writes_nothing()
    {
        await AuthenticateAsync();
        var from = Unique();

        var response = await _client.PostAsJsonAsync("/api/redirects/import",
            new { csv = $"{from},/somewhere", dryRun = true }, TestContext.Current.CancellationToken);

        JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .RootElement.GetProperty("created").GetInt32().Should().Be(1, "it reports what it would do");

        using var scope = _factory.Services.CreateScope();
        var stored = await scope.ServiceProvider.GetRequiredService<IQuerySession>()
            .Query<UrlRedirect>().Where(r => r.FromPath == from).ToListAsync(TestContext.Current.CancellationToken);

        stored.Should().BeEmpty("a dry run that wrote would be worse than no dry run");
    }
}
