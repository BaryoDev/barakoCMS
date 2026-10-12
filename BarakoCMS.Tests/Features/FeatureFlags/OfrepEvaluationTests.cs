using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BarakoCMS.FeatureFlags;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using OpenFeature;
using OpenFeature.Providers.Ofrep.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Features.FeatureFlags;

/// <summary>
/// The OFREP routes beside GET /api/feature-flags (#628). The first test is the ticket's done-when:
/// an OpenFeature SDK with the stock OFREP provider reads a flag, with no client of ours in between.
/// </summary>
[Collection("Sequential")]
public class OfrepEvaluationTests(IntegrationTestFixture factory)
{
    private const string Base = "/ofrep/v1/evaluate/flags";

    private async Task<string> FlagAsync(bool enabled, bool isPublic, List<string>? tenants = null, int rollout = 100)
    {
        var key = $"ofrep-{Guid.NewGuid():N}"[..20];
        using var scope = factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new FeatureFlag
        {
            Key = key,
            Enabled = enabled,
            IsPublic = isPublic,
            TenantSlugs = tenants ?? [],
            RolloutPercent = rollout,
        });
        await session.SaveChangesAsync();
        return key;
    }

    private static Task<HttpResponseMessage> EvaluateAsync(HttpClient client, string key, object? context = null) =>
        client.PostAsJsonAsync($"{Base}/{key}", new { context = context ?? new { } });

    [Fact]
    public async Task An_OpenFeature_SDK_with_the_OFREP_provider_reads_a_flag()
    {
        var on = await FlagAsync(enabled: true, isPublic: true);
        var off = await FlagAsync(enabled: false, isPublic: true);

        // Registered the way the provider's README says to, with one change: the named client's
        // handler is the test server's, since there is no port to point a real one at.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient("ofrep-test").ConfigurePrimaryHttpMessageHandler(() => factory.Server.CreateHandler());
        services.AddOpenFeature(builder => builder.AddOfrepProvider(o =>
        {
            o.BaseUrl = "http://localhost";
            o.HttpClientName = "ofrep-test";
        }));
        await using var provider = services.BuildServiceProvider();
        var lifecycle = provider.GetRequiredService<OpenFeature.Hosting.IFeatureLifecycleManager>();
        await lifecycle.EnsureInitializedAsync(CancellationToken.None);
        using var scope = provider.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<OpenFeature.IFeatureClient>();

        (await client.GetBooleanValueAsync(on, defaultValue: false)).Should().BeTrue();
        (await client.GetBooleanValueAsync(off, defaultValue: true)).Should().BeFalse("a disabled flag evaluates to false, not to the default");
        var missing = await client.GetBooleanDetailsAsync($"missing-{Guid.NewGuid():N}", defaultValue: true);
        missing.Value.Should().BeTrue("a flag that does not exist falls back to the caller's default");
        missing.ErrorType.Should().Be(OpenFeature.Constant.ErrorType.FlagNotFound);

        await lifecycle.ShutdownAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_flag_answers_in_the_OFREP_shape()
    {
        var key = await FlagAsync(enabled: true, isPublic: true);

        var response = await EvaluateAsync(factory.CreateClient(), key);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("key").GetString().Should().Be(key);
        doc.RootElement.GetProperty("value").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("reason").GetString().Should().Be("STATIC");
        doc.RootElement.GetProperty("variant").GetString().Should().Be("on");
    }

    [Fact]
    public async Task A_private_flag_is_not_found_for_an_anonymous_caller_and_found_when_signed_in()
    {
        var key = await FlagAsync(enabled: true, isPublic: false);

        var signedIn = factory.CreateClient();
        signedIn.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await factory.StoredUserTokenAsync("User"));
        (await EvaluateAsync(signedIn, key)).StatusCode.Should().Be(HttpStatusCode.OK, "the control: the flag exists");

        var anonymous = await EvaluateAsync(factory.CreateClient(), key);
        anonymous.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var doc = JsonDocument.Parse(await anonymous.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("errorCode").GetString().Should().Be("FLAG_NOT_FOUND");
    }

    [Fact]
    public async Task A_context_naming_another_tenant_does_not_change_the_evaluation()
    {
        var other = $"tenant-{Guid.NewGuid():N}"[..20];
        var key = await FlagAsync(enabled: true, isPublic: true, tenants: [other]);

        var response = await EvaluateAsync(factory.CreateClient(), key, new { targetingKey = other, tenant = other, tenantSlug = other });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("value").GetBoolean().Should().BeFalse("the tenant is the request's, never the context's");
        doc.RootElement.GetProperty("reason").GetString().Should().Be("TARGETING_MATCH");
    }

    [Fact]
    public async Task Bulk_evaluation_lists_public_flags_with_an_etag_and_answers_304_to_it()
    {
        var shown = await FlagAsync(enabled: true, isPublic: true);
        var hidden = await FlagAsync(enabled: true, isPublic: false);
        var client = factory.CreateClient();

        var first = await client.PostAsJsonAsync(Base, new { context = new { } });
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await first.Content.ReadAsStringAsync();
        using (var doc = JsonDocument.Parse(body))
        {
            var keys = doc.RootElement.GetProperty("flags").EnumerateArray().Select(f => f.GetProperty("key").GetString()).ToList();
            keys.Should().NotBeEmpty();
            keys.Should().Contain(shown);
            keys.Should().NotContain(hidden, "an anonymous caller is not told a private flag's key");
        }

        var etag = first.Headers.ETag;
        etag.Should().NotBeNull();
        using var again = new HttpRequestMessage(HttpMethod.Post, Base) { Content = JsonContent.Create(new { context = new { } }) };
        again.Headers.IfNoneMatch.Add(etag!);
        (await client.SendAsync(again)).StatusCode.Should().Be(HttpStatusCode.NotModified);
    }

    [Fact]
    public async Task The_existing_route_still_answers_and_names_its_successor()
    {
        var response = await factory.CreateClient().GetAsync("/api/feature-flags");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues("Link").Single().Should().Be($"<{Base}>; rel=\"successor-version\"");
    }
}
