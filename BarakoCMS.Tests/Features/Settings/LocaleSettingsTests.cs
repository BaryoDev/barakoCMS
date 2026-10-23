using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using barakoCMS.Features.Settings.Locales;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Features.Settings;

/// <summary>A tenant's languages: stored per tenant, validated, and admin only.</summary>
[Collection("Sequential")]
public class LocaleSettingsTests
{
    private readonly IntegrationTestFixture _factory;
    private static int _ipCounter;

    public LocaleSettingsTests(IntegrationTestFixture factory) => _factory = factory;

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..20].ToLowerInvariant();

    private static UpdateLocaleSettingsRequest Valid() => new()
    {
        DefaultLocale = "en",
        Locales = ["en", "fil", "pt-BR"],
        FallbackChains = new() { ["pt-BR"] = ["en"], ["fil"] = ["en"] },
    };

    [Fact]
    public async Task A_tenant_with_no_locale_settings_gets_an_empty_answer()
    {
        var admin = await ClientAsync(await TenantAsync(), SystemRoles.AdminRoleId, "Admin");

        var res = await admin.GetAsync("/api/settings/locales", TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        body.GetProperty("defaultLocale").GetString().Should().BeEmpty();
        body.GetProperty("locales").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Locale_settings_put_by_an_admin_are_read_back_and_shown_in_meta()
    {
        var admin = await ClientAsync(await TenantAsync(), SystemRoles.AdminRoleId, "Admin");

        var put = await admin.PutAsJsonAsync("/api/settings/locales", Valid(), TestContext.Current.CancellationToken);
        put.StatusCode.Should().Be(HttpStatusCode.OK, await put.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        var body = await (await admin.GetAsync("/api/settings/locales", TestContext.Current.CancellationToken))
            .Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        body.GetProperty("defaultLocale").GetString().Should().Be("en");
        var locales = body.GetProperty("locales").EnumerateArray().Select(e => e.GetString()).ToList();
        locales.Should().NotBeEmpty();
        locales.Should().Equal("en", "fil", "pt-BR");
        body.GetProperty("fallbackChains").GetProperty("pt-BR")[0].GetString().Should().Be("en");

        var meta = await (await admin.GetAsync("/api/meta", TestContext.Current.CancellationToken))
            .Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        meta.GetProperty("locales").GetProperty("defaultLocale").GetString().Should().Be("en");
        meta.GetProperty("locales").GetProperty("locales").GetArrayLength().Should().Be(3);
    }

    [Fact]
    public async Task A_default_locale_outside_the_list_is_rejected()
    {
        var admin = await ClientAsync(await TenantAsync(), SystemRoles.AdminRoleId, "Admin");
        var req = Valid();
        req.DefaultLocale = "de";

        (await admin.PutAsJsonAsync("/api/settings/locales", req, TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_fallback_to_an_unknown_code_is_rejected()
    {
        var admin = await ClientAsync(await TenantAsync(), SystemRoles.AdminRoleId, "Admin");
        var req = Valid();
        req.FallbackChains["fil"] = ["xx"];

        (await admin.PutAsJsonAsync("/api/settings/locales", req, TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_fallback_cycle_is_rejected()
    {
        var admin = await ClientAsync(await TenantAsync(), SystemRoles.AdminRoleId, "Admin");
        var req = Valid();
        req.FallbackChains = new() { ["fil"] = ["pt-BR"], ["pt-BR"] = ["fil"] };

        (await admin.PutAsJsonAsync("/api/settings/locales", req, TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_code_that_is_not_language_shaped_is_rejected()
    {
        var admin = await ClientAsync(await TenantAsync(), SystemRoles.AdminRoleId, "Admin");
        var req = Valid();
        req.Locales.Add("not a code");

        (await admin.PutAsJsonAsync("/api/settings/locales", req, TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_caller_without_the_admin_role_gets_403_on_read_and_write()
    {
        var user = await ClientAsync(await TenantAsync(), SystemRoles.UserRoleId, "User");

        (await user.GetAsync("/api/settings/locales", TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await user.PutAsJsonAsync("/api/settings/locales", Valid(), TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task One_tenants_locales_are_not_visible_to_another()
    {
        var a = await ClientAsync(await TenantAsync(), SystemRoles.AdminRoleId, "Admin");
        var b = await ClientAsync(await TenantAsync(), SystemRoles.AdminRoleId, "Admin");

        (await a.PutAsJsonAsync("/api/settings/locales", Valid(), TestContext.Current.CancellationToken))
            .IsSuccessStatusCode.Should().BeTrue();

        var seenByB = await (await b.GetAsync("/api/settings/locales", TestContext.Current.CancellationToken))
            .Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        seenByB.GetProperty("locales").GetArrayLength().Should().Be(0);
        seenByB.GetProperty("defaultLocale").GetString().Should().BeEmpty();
    }

    private async Task StoreAsync(params object[] documents)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentStore>().LightweightSession();
        foreach (var document in documents) session.Store(document);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<string> TenantAsync()
    {
        var slug = Unique("loc");
        await StoreAsync(new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = slug });
        return slug;
    }

    private async Task<HttpClient> ClientAsync(string slug, Guid roleId, string roleName)
    {
        var userId = Guid.NewGuid();
        var username = Unique("u");
        await StoreAsync(
            new User { Id = userId, Username = username, Email = $"{Unique("m")}@example.com" },
            new Membership
            {
                Id = Guid.NewGuid(), UserId = userId, TenantSlug = slug,
                Status = MembershipStatus.Active, RoleIds = [roleId],
            });

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            _factory.CreateToken(
                roles: [roleName],
                userId: userId.ToString(),
                additionalClaims: new Dictionary<string, string> { ["tenant"] = slug, ["Username"] = username }));
        client.DefaultRequestHeaders.Add("X-Tenant", slug);
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, $"198.51.102.{Interlocked.Increment(ref _ipCounter) % 250 + 1}");
        return client;
    }
}
