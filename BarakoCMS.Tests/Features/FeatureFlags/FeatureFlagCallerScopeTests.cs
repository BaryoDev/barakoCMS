using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BarakoCMS.FeatureFlags;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Features.FeatureFlags;

/// <summary>
/// A flag is targeted by the email stored on the user's record, not by the username they chose, and
/// a flag limited to some tenants is not listed to a caller of another one.
/// </summary>
[Collection("Integration1")]
public class FeatureFlagCallerScopeTests(IntegrationTestFixture factory)
{
    private static int _ipCounter;

    private async Task StoreAsync(params object[] documents)
    {
        using var scope = factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        foreach (var document in documents) session.Store(document);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..20].ToLowerInvariant();

    private HttpClient Client(User? user = null, string? tenant = null)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(
            TestRemoteIpFilter.Header, $"198.51.100.{Interlocked.Increment(ref _ipCounter) % 200 + 20}");
        if (tenant is not null) client.DefaultRequestHeaders.Add("X-Tenant", tenant);
        if (user is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
                factory.CreateToken(
                    roles: ["User"],
                    userId: user.Id.ToString(),
                    additionalClaims: new Dictionary<string, string> { ["Username"] = user.Username }));
        }
        return client;
    }

    private static User NewUser(string username, string email) =>
        new() { Id = Guid.NewGuid(), Username = username, Email = email };

    private static async Task<Dictionary<string, bool>> EvaluateAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/feature-flags", TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<Dictionary<string, bool>>(
            TestContext.Current.CancellationToken))!;
    }

    private static async Task<List<string>> OfrepKeysAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(
            "/ofrep/v1/evaluate/flags", new { context = new { } }, TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return doc.RootElement.GetProperty("flags").EnumerateArray()
            .Select(f => f.GetProperty("key").GetString()!).ToList();
    }

    [Fact]
    public async Task A_username_equal_to_a_targeted_address_does_not_get_the_flag()
    {
        var targeted = $"{Unique("target")}@example.com";
        var key = Unique("email-flag");
        await StoreAsync(new FeatureFlag { Key = key, Enabled = true, IsPublic = true, UserEmails = [targeted] });
        var impostor = NewUser(targeted, $"{Unique("other")}@example.com");
        var owner = NewUser(Unique("owner"), targeted);
        await StoreAsync(impostor, owner);

        var ownerFlags = await EvaluateAsync(Client(owner));
        ownerFlags.Should().ContainKey(key);
        ownerFlags[key].Should().BeTrue("the control: the stored email matches the target");

        var flags = await EvaluateAsync(Client(impostor));
        flags.Should().ContainKey(key);
        flags[key].Should().BeFalse("the username is chosen by the user, the stored email is not");
    }

    [Fact]
    public async Task The_OFREP_route_does_not_target_by_username_either()
    {
        var targeted = $"{Unique("target")}@example.com";
        var key = Unique("ofrep-email");
        await StoreAsync(new FeatureFlag { Key = key, Enabled = true, IsPublic = true, UserEmails = [targeted] });
        var impostor = NewUser(targeted, $"{Unique("other")}@example.com");
        var owner = NewUser(Unique("owner"), targeted);
        await StoreAsync(impostor, owner);

        async Task<bool> ValueAsync(HttpClient client)
        {
            var response = await client.PostAsJsonAsync(
                $"/ofrep/v1/evaluate/flags/{key}", new { context = new { } }, TestContext.Current.CancellationToken);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            return doc.RootElement.GetProperty("value").GetBoolean();
        }

        (await ValueAsync(Client(owner))).Should().BeTrue("the control: the stored email matches the target");
        (await ValueAsync(Client(impostor))).Should().BeFalse();
    }

    [Fact]
    public async Task A_flag_limited_to_one_tenant_is_not_listed_to_another()
    {
        var tenantA = Unique("flag-a");
        var tenantB = Unique("flag-b");
        await StoreAsync(
            new Tenant { Id = Guid.NewGuid(), Slug = tenantA, Name = tenantA },
            new Tenant { Id = Guid.NewGuid(), Slug = tenantB, Name = tenantB });
        var onlyA = Unique("only-a");
        var everyone = Unique("everyone");
        await StoreAsync(
            new FeatureFlag { Key = onlyA, Enabled = true, IsPublic = true, TenantSlugs = [tenantA] },
            new FeatureFlag { Key = everyone, Enabled = true, IsPublic = true });

        var fromA = await EvaluateAsync(Client(tenant: tenantA));
        fromA.Should().ContainKey(onlyA);
        fromA.Should().ContainKey(everyone);

        var fromB = await EvaluateAsync(Client(tenant: tenantB));
        fromB.Should().ContainKey(everyone, "the control: the list is not simply empty");
        fromB.Should().NotContainKey(onlyA);

        var ofrepA = await OfrepKeysAsync(Client(tenant: tenantA));
        ofrepA.Should().Contain(onlyA);

        var ofrepB = await OfrepKeysAsync(Client(tenant: tenantB));
        ofrepB.Should().Contain(everyone);
        ofrepB.Should().NotContain(onlyA);
    }
}
