using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using BarakoCMS.FeatureFlags;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Features.FeatureFlags;

/// <summary>
/// A percentage rollout has to give the same person the same answer every time, and the admin
/// routes have to stay shut to everyone else.
/// </summary>
/// <remarks>
/// A flag that re-rolls per request is worse than no flag at all: the feature flickers on and off
/// between two requests of the same page load, so half a UI renders against one branch and half
/// against the other. It also makes a rollout unmeasurable, because nobody is in the group for
/// longer than one request.
/// </remarks>
[Collection("Sequential")]
public class FeatureFlagRolloutTests
{
    private static int _ipCounter;

    private readonly IntegrationTestFixture _factory;

    public FeatureFlagRolloutTests(IntegrationTestFixture factory) => _factory = factory;

    private async Task StoreAsync(FeatureFlag flag)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(flag);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private HttpClient ClientFor(string username)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(
                roles: ["User"],
                additionalClaims: new Dictionary<string, string> { ["Username"] = username }));
        // Its own bucket, because the stability test asks the same question several times over and
        // the global limiter counts per address across the whole suite.
        client.DefaultRequestHeaders.Add(
            TestRemoteIpFilter.Header, $"198.51.100.{Interlocked.Increment(ref _ipCounter) % 200 + 20}");
        return client;
    }

    private async Task<Dictionary<string, bool>> EvaluateAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/feature-flags", TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<Dictionary<string, bool>>(
            TestContext.Current.CancellationToken))!;
    }

    /// <summary>
    /// The same caller asking five times in a row gets one answer, not five coin flips.
    /// </summary>
    [Fact]
    public async Task A_partial_rollout_gives_the_same_caller_the_same_answer_every_time()
    {
        var key = $"rollout-{Guid.NewGuid():N}";
        await StoreAsync(new FeatureFlag { Key = key, Enabled = true, IsPublic = true, RolloutPercent = 50 });

        var client = ClientFor($"steady-{Guid.NewGuid():N}@example.com");
        var first = (await EvaluateAsync(client))[key];

        for (var attempt = 0; attempt < 4; attempt++)
        {
            (await EvaluateAsync(client))[key].Should().Be(first,
                "a flag that flickers between requests renders half a page against each branch");
        }
    }

    /// <summary>
    /// The same statement, made where it can be repeated enough times to mean something.
    /// </summary>
    /// <remarks>
    /// The test above sends five requests, and five requests is not many: an evaluator that tossed a
    /// coin per call would still come up the same way five times in a row often enough to slip
    /// through, which it did the first time this was checked against a deliberately randomised
    /// bucket. The bucketing is a pure function, so asking it directly, many times, over many
    /// subjects, turns "probably stable" into stable.
    /// </remarks>
    [Fact]
    public void The_bucket_depends_on_the_subject_and_on_nothing_else()
    {
        var flag = new FeatureFlag { Key = "steady", Enabled = true, RolloutPercent = 50 };

        foreach (var subject in Enumerable.Range(0, 20).Select(n => $"steady-{n}@example.com"))
        {
            var context = new FlagContext("default", subject, subject);
            var answers = Enumerable.Range(0, 50)
                .Select(_ => FeatureFlagService.Evaluate(flag, context))
                .Distinct()
                .ToArray();

            answers.Should().ContainSingle(
                "one subject, one flag, one answer, however many times you ask");
        }
    }

    /// <summary>
    /// And a half rollout is actually a half: it neither includes everyone nor nobody.
    /// </summary>
    /// <remarks>
    /// Evaluated directly over many subjects rather than over HTTP, because the claim is about the
    /// bucketing function and a handful of requests could land on one side by luck. The bucket is a
    /// hash, so this is deterministic rather than statistical: the same subjects always split the
    /// same way.
    /// </remarks>
    [Fact]
    public void A_half_rollout_splits_the_population()
    {
        var flag = new FeatureFlag { Key = "half", Enabled = true, RolloutPercent = 50 };
        var subjects = Enumerable.Range(0, 200).Select(n => $"person-{n}@example.com").ToArray();

        var included = subjects.Count(s => FeatureFlagService.Evaluate(flag, new FlagContext("default", s, s)));

        included.Should().BeGreaterThan(0, "nobody in is not a rollout, it is an off switch");
        included.Should().BeLessThan(subjects.Length, "everybody in is not a rollout either");
    }

    /// <summary>
    /// The two ends, so that "stable" cannot be satisfied by an evaluator that answers the same
    /// thing to everybody.
    /// </summary>
    [Fact]
    public void The_ends_of_the_range_mean_what_they_say()
    {
        var subjects = Enumerable.Range(0, 50).Select(n => $"edge-{n}@example.com").ToArray();

        var off = new FeatureFlag { Key = "none", Enabled = true, RolloutPercent = 0 };
        var on = new FeatureFlag { Key = "all", Enabled = true, RolloutPercent = 100 };

        subjects.Should().OnlyContain(s => !FeatureFlagService.Evaluate(off, new FlagContext("default", s, s)));
        subjects.Should().OnlyContain(s => FeatureFlagService.Evaluate(on, new FlagContext("default", s, s)));
    }

    /// <summary>
    /// The admin routes hand over the targeting rules, which is exactly what the public endpoint
    /// withholds, so they are shut to anyone without an admin role.
    /// </summary>
    [Fact]
    public async Task The_admin_routes_are_closed_to_a_caller_without_an_admin_role()
    {
        var key = $"guarded-{Guid.NewGuid():N}";
        await StoreAsync(new FeatureFlag { Key = key, Enabled = true, RolloutPercent = 50 });

        var user = ClientFor($"nobody-{Guid.NewGuid():N}@example.com");

        (await user.GetAsync("/api/feature-flags/admin", TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden,
                "the full catalogue names every unreleased feature and who is being targeted with it");

        (await user.PostAsJsonAsync("/api/feature-flags/admin", new { key, enabled = true, isPublic = true },
                TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden,
                "writing is how a caller would flip themselves into a flag they are not in");

        (await user.PostAsync($"/api/feature-flags/admin/{key}/toggle", null,
                TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        (await user.DeleteAsync($"/api/feature-flags/admin/{key}", TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// Anonymous is refused too, and refused as unauthenticated rather than as not found.
    /// </summary>
    [Fact]
    public async Task The_admin_routes_are_closed_to_an_anonymous_caller()
    {
        (await _factory.CreateClient().GetAsync(
                "/api/feature-flags/admin", TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// The control for the two above. An admin can still do all of it, so the guard is a guard and
    /// not an outage.
    /// </summary>
    [Fact]
    public async Task An_admin_still_reaches_the_admin_routes()
    {
        var (token, _) = await TestHelpers.CreateAdminUserAsync(_factory);
        var admin = _factory.CreateClient();
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        (await admin.GetAsync("/api/feature-flags/admin", TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// And the rules themselves never appear on the anonymous endpoint, whatever the decision was.
    /// </summary>
    /// <remarks>
    /// The public endpoint answers with a flat map of key to boolean. A response that also carried
    /// <c>rolloutPercent</c> or <c>userEmails</c> would tell a caller who is being targeted and by
    /// how much, which is the roadmap the endpoint is careful about elsewhere.
    /// </remarks>
    [Fact]
    public async Task The_public_endpoint_returns_decisions_and_never_the_targeting_rules()
    {
        var key = $"targeted-{Guid.NewGuid():N}";
        var insider = $"insider-{Guid.NewGuid():N}@example.com";
        await StoreAsync(new FeatureFlag
        {
            Key = key,
            Enabled = true,
            IsPublic = true,
            RolloutPercent = 50,
            UserEmails = [insider],
        });

        var body = await (await _factory.CreateClient().GetAsync(
                "/api/feature-flags", TestContext.Current.CancellationToken))
            .Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        body.Should().Contain(key, "the control: the flag is in the response at all");
        body.Should().NotContain(insider, "naming the people being targeted is the leak");
        body.Should().NotContain("rolloutPercent");
        body.Should().NotContain("userEmails");
        body.Should().NotContain("tenantSlugs");
    }
}
