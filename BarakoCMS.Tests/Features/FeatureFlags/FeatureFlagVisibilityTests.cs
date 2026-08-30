using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using BarakoCMS.FeatureFlags;

namespace BarakoCMS.Tests.Features.FeatureFlags;

/// <summary>
/// GET /api/feature-flags is anonymous on purpose: a public page rendering with flags has no user to
/// authenticate. So the boundary is per flag, not per endpoint, and the thing being kept back is the
/// key rather than the value. Targeting already evaluates a private flag to false for a stranger,
/// and <c>{"acquisition-of-northwind": false}</c> tells them everything anyway.
/// </summary>
[Collection("Sequential")]
public class FeatureFlagVisibilityTests
{
    private readonly IntegrationTestFixture _factory;

    public FeatureFlagVisibilityTests(IntegrationTestFixture factory) => _factory = factory;

    private async Task StoreAsync(params FeatureFlag[] flags)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        foreach (var flag in flags) session.Store(flag);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static FeatureFlag Flag(string key, bool isPublic) => new()
    {
        Key = key,
        Enabled = true,
        IsPublic = isPublic,
    };

    private async Task<Dictionary<string, bool>> EvaluateAsync(string? token = null)
    {
        var client = _factory.CreateClient();
        if (token is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var res = await client.GetAsync("/api/feature-flags", TestContext.Current.CancellationToken);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await res.Content.ReadFromJsonAsync<Dictionary<string, bool>>(
            TestContext.Current.CancellationToken))!;
    }

    // The positive control. Every assertion below is satisfied by an endpoint that returns an empty
    // dictionary to everyone, which would be a broken endpoint rather than a fixed one.
    [Fact]
    public async Task An_anonymous_caller_receives_the_flags_marked_public()
    {
        var published = $"published-{Guid.NewGuid():N}";
        await StoreAsync(Flag(published, isPublic: true));

        var flags = await EvaluateAsync();

        flags.Should().ContainKey(published);
        flags[published].Should().BeTrue("an enabled public flag is exactly what this endpoint exists to serve");
    }

    [Fact]
    public async Task An_anonymous_caller_is_not_told_the_key_of_a_private_flag()
    {
        var secret = $"secret-{Guid.NewGuid():N}";
        await StoreAsync(Flag(secret, isPublic: false));

        var flags = await EvaluateAsync();

        // Absent, not false. A false value still hands over the name, which is the whole leak.
        flags.Should().NotContainKey(secret);
    }

    [Fact]
    public async Task An_authenticated_caller_still_receives_everything()
    {
        var secret = $"secret-{Guid.NewGuid():N}";
        var published = $"published-{Guid.NewGuid():N}";
        await StoreAsync(Flag(secret, isPublic: false), Flag(published, isPublic: true));
        var (token, _) = await TestHelpers.CreateAdminUserAsync(_factory);

        var flags = await EvaluateAsync(token);

        flags.Should().ContainKey(secret, "signing in is what the full catalogue is gated behind");
        flags.Should().ContainKey(published);
    }

    [Fact]
    public async Task A_flag_created_without_saying_it_is_public_is_not_public()
    {
        var key = $"unstated-{Guid.NewGuid():N}";
        var (token, _) = await TestHelpers.CreateAdminUserAsync(_factory);
        var admin = _factory.CreateClient();
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // The body a client written before this field existed sends.
        var created = await admin.PostAsJsonAsync(
            "/api/feature-flags/admin",
            new { key, enabled = true },
            TestContext.Current.CancellationToken);
        created.StatusCode.Should().Be(HttpStatusCode.OK);

        (await EvaluateAsync()).Should().NotContainKey(key,
            "an unstated flag stays private, so upgrading does not publish the existing catalogue");
    }

    [Fact]
    public async Task An_admin_can_publish_a_flag_deliberately()
    {
        var key = $"stated-{Guid.NewGuid():N}";
        var (token, _) = await TestHelpers.CreateAdminUserAsync(_factory);
        var admin = _factory.CreateClient();
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var created = await admin.PostAsJsonAsync(
            "/api/feature-flags/admin",
            new { key, enabled = true, isPublic = true },
            TestContext.Current.CancellationToken);
        created.StatusCode.Should().Be(HttpStatusCode.OK);

        (await EvaluateAsync()).Should().ContainKey(key,
            "the private default is only useful if there is a way out of it");
    }
}
