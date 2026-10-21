using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Features.ApiKeys;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;

namespace BarakoCMS.Tests;

[Collection("Sequential")]
public class ApiKeyUsageTests(IntegrationTestFixture factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    private async Task<Guid> OwnerAsync()
    {
        using var scope = factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var userId = Guid.NewGuid();
        session.Store(new User { Id = userId, Username = $"owner-{userId}", Email = $"owner-{userId}@example.com", RoleIds = [] });
        await session.SaveChangesAsync();
        return userId;
    }

    private async Task<(Guid id, string secret)> StoreKeyAsync(
        string[] scopes, string tenant = Tenant.DefaultSlug, string[]? contentTypes = null,
        bool revoked = false, DateTime? expiresAt = null)
    {
        var secret = "bcms_" + Guid.NewGuid().ToString("N");
        var id = Guid.NewGuid();
        var owner = await OwnerAsync();
        using var scope = factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new ApiKey
        {
            Id = id, Name = "usage-test", KeyHash = ApiKeyService.Hash(secret), Prefix = secret[..12],
            UserId = owner, TenantSlug = tenant, Scopes = scopes.ToList(),
            ContentTypes = contentTypes?.ToList() ?? [], Revoked = revoked, ExpiresAt = expiresAt,
        });
        await session.SaveChangesAsync();
        return (id, secret);
    }

    private async Task<HttpStatusCode> UseKeyAsync(string secret, HttpMethod method, string path)
    {
        var req = new HttpRequestMessage(method, path) { Content = method == HttpMethod.Get ? null : JsonContent.Create(new { }) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        return (await _client.SendAsync(req)).StatusCode;
    }

    private async Task<HttpResponseMessage> UsageAsync(Guid keyId, string? jwt)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/api-keys/{keyId}/usage");
        if (jwt is not null)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        return await _client.SendAsync(req);
    }

    private async Task<JsonElement> UsageBodyAsync(Guid keyId)
    {
        var res = await UsageAsync(keyId, await factory.StoredUserTokenAsync("Admin"));
        res.StatusCode.Should().Be(HttpStatusCode.OK, because: await res.Content.ReadAsStringAsync());
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task A_key_refused_for_a_missing_scope_shows_the_reason_in_its_usage()
    {
        var (id, secret) = await StoreKeyAsync(["content:read"]);

        (await UseKeyAsync(secret, HttpMethod.Post, "/api/contents")).Should().Be(HttpStatusCode.Forbidden);

        var usage = await UsageBodyAsync(id);
        usage.GetProperty("totalRefusals").GetInt32().Should().Be(1);
        usage.GetProperty("refusalsByReason").GetProperty("scope").GetInt32().Should().Be(1);
        usage.GetProperty("recentRefusals").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task A_push_key_sent_to_another_content_type_is_counted_by_reason_and_type()
    {
        var (id, secret) = await StoreKeyAsync(["content:write"], contentTypes: ["site-pages"]);

        (await UseKeyAsync(secret, HttpMethod.Post, "/api/collections/other-type/push")).Should().Be(HttpStatusCode.Forbidden);

        var usage = await UsageBodyAsync(id);
        usage.GetProperty("refusalsByReason").GetProperty("content_type").GetInt32().Should().Be(1);
        usage.GetProperty("refusalsByContentType").GetProperty("other-type").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Revoked_and_expired_keys_are_counted_under_their_own_reasons()
    {
        var (revokedId, revokedSecret) = await StoreKeyAsync(["content:read"], revoked: true);
        var (expiredId, expiredSecret) = await StoreKeyAsync(["content:read"], expiresAt: DateTime.UtcNow.AddMinutes(-1));

        (await UseKeyAsync(revokedSecret, HttpMethod.Get, "/api/contents")).Should().Be(HttpStatusCode.Unauthorized);
        (await UseKeyAsync(expiredSecret, HttpMethod.Get, "/api/contents")).Should().Be(HttpStatusCode.Unauthorized);

        (await UsageBodyAsync(revokedId)).GetProperty("refusalsByReason").GetProperty("revoked").GetInt32().Should().Be(1);
        (await UsageBodyAsync(expiredId)).GetProperty("refusalsByReason").GetProperty("expired").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task A_secret_that_matches_no_key_is_refused_and_leaves_nothing_behind()
    {
        var (id, _) = await StoreKeyAsync(["content:read"]);
        var tracked = factory.Services.GetRequiredService<ApiKeyRefusals>().TrackedKeys;

        (await UseKeyAsync("bcms_" + Guid.NewGuid().ToString("N"), HttpMethod.Get, "/api/contents")).Should().Be(HttpStatusCode.Unauthorized);

        (await UsageBodyAsync(id)).GetProperty("totalRefusals").GetInt32().Should().Be(0);
        factory.Services.GetRequiredService<ApiKeyRefusals>().TrackedKeys.Should().Be(tracked);
    }

    [Fact]
    public async Task Usage_lists_the_content_types_the_key_names_that_no_longer_exist()
    {
        var (id, _) = await StoreKeyAsync(["content:write"], contentTypes: [$"gone-{Guid.NewGuid():N}"[..12]]);

        var usage = await UsageBodyAsync(id);

        usage.GetProperty("missingContentTypes").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task Another_tenants_key_is_not_visible()
    {
        var (id, _) = await StoreKeyAsync(["content:read"], tenant: "some-other-tenant");

        var res = await UsageAsync(id, await factory.StoredUserTokenAsync("Admin"));

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Usage_is_401_for_anonymous_and_403_for_a_caller_without_the_gate()
    {
        var (id, _) = await StoreKeyAsync(["content:read"]);

        (await UsageAsync(id, null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await UsageAsync(id, await factory.StoredUserTokenAsync("User"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_content_type_change_names_the_live_keys_that_use_the_type()
    {
        var type = $"warn-{Guid.NewGuid():N}"[..14];
        var (named, _) = await StoreKeyAsync(["content:write"], contentTypes: [type.ToUpperInvariant()]);
        await StoreKeyAsync(["content:write"], contentTypes: ["unrelated"]);
        await StoreKeyAsync(["content:write"], contentTypes: [type], revoked: true);
        await StoreKeyAsync(["content:write"], tenant: "some-other-tenant", contentTypes: [type]);

        using var scope = factory.Services.CreateScope();
        var warnings = await ApiKeyContentTypeImpact.KeysNamingAsync(
            scope.ServiceProvider.GetRequiredService<IQuerySession>(), Tenant.DefaultSlug, [type], CancellationToken.None);

        warnings.Should().ContainSingle().Which.KeyId.Should().Be(named);
    }

    [Fact]
    public async Task A_push_to_a_type_that_does_not_exist_is_counted_as_other_not_as_the_path_text()
    {
        var (id, secret) = await StoreKeyAsync(["content:write"], contentTypes: ["site-pages"]);

        await UseKeyAsync(secret, HttpMethod.Post, "/api/collections/%3Cscript%3E/push");

        var by = (await UsageBodyAsync(id)).GetProperty("refusalsByContentType");
        by.GetProperty("other").GetInt32().Should().Be(1);
        by.EnumerateObject().Should().ContainSingle();
    }

    [Fact]
    public async Task A_push_to_an_existing_type_the_key_does_not_name_is_recorded_under_that_type()
    {
        var type = $"exists{Guid.NewGuid():N}"[..14];
        using (var scope = factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new ContentTypeDefinition { Id = Guid.NewGuid(), Name = type, DisplayName = "x", Fields = [] });
            await session.SaveChangesAsync();
        }
        var (id, secret) = await StoreKeyAsync(["content:write"], contentTypes: ["site-pages"]);

        await UseKeyAsync(secret, HttpMethod.Post, $"/api/collections/{type}/push");

        (await UsageBodyAsync(id)).GetProperty("refusalsByContentType").GetProperty(type).GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task A_key_naming_a_type_in_another_case_is_not_reported_missing()
    {
        var type = $"cased{Guid.NewGuid():N}"[..14];
        using (var scope = factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new ContentTypeDefinition { Id = Guid.NewGuid(), Name = type, DisplayName = "x", Fields = [] });
            await session.SaveChangesAsync();
        }
        var (id, _) = await StoreKeyAsync(["content:write"], contentTypes: [type.ToUpperInvariant()]);

        (await UsageBodyAsync(id)).GetProperty("missingContentTypes").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public void An_idle_key_is_swept_when_the_cap_is_reached_so_new_keys_are_still_counted()
    {
        var clock = new Clock(DateTimeOffset.UtcNow);
        var refusals = new ApiKeyRefusals(clock);
        for (var i = 0; i < ApiKeyRefusals.MaxKeys; i++)
            refusals.Record(Guid.NewGuid(), ApiKeyRefusalReason.Scope);
        refusals.Record(Guid.NewGuid(), ApiKeyRefusalReason.Scope);
        refusals.TrackedKeys.Should().Be(ApiKeyRefusals.MaxKeys, "nothing is old enough to sweep yet");

        clock.Now = clock.Now.AddHours(25);
        var fresh = Guid.NewGuid();
        refusals.Record(fresh, ApiKeyRefusalReason.Scope);

        refusals.Recent(fresh).Should().HaveCount(1);
        refusals.TrackedKeys.Should().Be(1);
    }

    [Fact]
    public void The_window_drops_old_entries_and_each_key_is_capped()
    {
        var clock = new Clock(DateTimeOffset.UtcNow);
        var refusals = new ApiKeyRefusals(clock);
        var key = Guid.NewGuid();

        refusals.Record(key, ApiKeyRefusalReason.Scope);
        clock.Now = clock.Now.AddHours(23);
        refusals.Record(key, ApiKeyRefusalReason.Revoked);
        refusals.Recent(key).Should().HaveCount(2);

        clock.Now = clock.Now.AddHours(2);
        var left = refusals.Recent(key);
        left.Should().HaveCount(1);
        left[0].Reason.Should().Be(ApiKeyRefusalReason.Revoked);

        for (var i = 0; i < ApiKeyRefusals.MaxPerKey + 50; i++)
            refusals.Record(key, ApiKeyRefusalReason.Scope, new string('x', 500));
        var capped = refusals.Recent(key);
        capped.Should().HaveCount(ApiKeyRefusals.MaxPerKey);
        capped[0].ContentType!.Length.Should().Be(ApiKeyRefusals.MaxContentTypeLength);
    }

    private sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
