using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests;

/// <summary>
/// A signed-in user in one tenant cannot read another tenant's content over HTTP.
/// </summary>
/// <remarks>
/// Two things were already tested well and separately, and nothing tested the join.
///
/// `TenantIsolationTests` opens two Marten sessions scoped to different tenants and proves neither
/// can see the other's documents, which is the partitioning primitive. `CrossTenantTokenTests`
/// proves a user cannot mint a token for a tenant they do not belong to, which is the credential.
///
/// Between them sits `TenantResolutionMiddleware` and `TenantSessionFactory`, threading the resolved
/// tenant into the session a live request uses. If that threading broke, both existing suites would
/// still pass in full: one never issues an HTTP request, the other never reads content. For a
/// product whose pitch includes multi-tenancy, this is the join most worth having a test for.
///
/// Every assertion goes through `ShouldBeRejectedOnMerits`, so a 429 from a shared rate-limit bucket
/// cannot stand in for an authorisation refusal. That distinction has bitten this suite before.
/// </remarks>
[Collection("Sequential")]
public class CrossTenantContentApiTests
{
    private readonly IntegrationTestFixture _factory;

    public CrossTenantContentApiTests(IntegrationTestFixture factory) => _factory = factory;

    private async Task<string> TenantAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var slug = $"club-{Guid.NewGuid():N}"[..14].ToLowerInvariant();
        session.Store(new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = slug, IsActive = true });
        await session.SaveChangesAsync();
        return slug;
    }

    private async Task<HttpClient> MemberAsync(string tenantSlug, string ip)
    {
        var userId = Guid.NewGuid();

        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new User
            {
                Id = userId,
                Username = $"xt-{Guid.NewGuid():n}"[..14],
                Email = $"xt-{Guid.NewGuid():n}@example.com",
                RoleIds = [barakoCMS.Models.SystemRoles.SuperAdminRoleId],
            });
            session.Store(new Membership
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                TenantSlug = tenantSlug,
                Status = MembershipStatus.Active,
                RoleIds = [barakoCMS.Models.SystemRoles.SuperAdminRoleId],
            });
            await session.SaveChangesAsync();
        }

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(
                roles: ["SuperAdmin"],
                userId: userId.ToString(),
                additionalClaims: new Dictionary<string, string> { ["tenant"] = tenantSlug }));
        client.DefaultRequestHeaders.Add("X-Tenant", tenantSlug);
        // Its own rate-limit bucket. A 429 here would be indistinguishable from a refusal.
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, ip);
        return client;
    }

    /// <summary>Stores content directly in a tenant's partition, the way the API would.</summary>
    private async Task<Guid> ContentInAsync(string tenantSlug, string type, string title)
    {
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession(tenantSlug);

        var id = Guid.NewGuid();
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(), Name = type, DisplayName = type,
            Fields = [new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string" }],
        });
        session.Store(new Content
        {
            Id = id, ContentType = type,
            Status = ContentStatus.Published, Sensitivity = SensitivityLevel.Public,
            Data = new Dictionary<string, object> { ["Title"] = title },
        });
        await session.SaveChangesAsync();
        return id;
    }

    private static void ShouldBeRejectedOnMerits(HttpResponseMessage resp, string because)
    {
        resp.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests,
            "a rate-limited response proves nothing about isolation, the test is not exercising what it claims");
        resp.IsSuccessStatusCode.Should().BeFalse(because);
    }

    [Fact]
    public async Task A_member_of_one_tenant_cannot_fetch_another_tenants_content_by_id()
    {
        var acme = await TenantAsync();
        var globex = await TenantAsync();

        var type = $"xt_{Guid.NewGuid():n}"[..12];
        var globexContent = await ContentInAsync(globex, type, "globex secret");

        var acmeClient = await MemberAsync(acme, "203.0.113.91");

        var resp = await acmeClient.GetAsync($"/api/contents/{globexContent}");

        ShouldBeRejectedOnMerits(resp,
            "the id is real and the caller is a SuperAdmin, in the wrong tenant. Only the partition "
          + "threaded into the session for this request stands between them");
    }

    /// <summary>
    /// The control, and it is the reason the test above means anything.
    /// </summary>
    /// <remarks>
    /// Without it, tenant resolution failing outright and returning 404 for everything would satisfy
    /// every refusal assertion in this file while breaking the product completely.
    /// </remarks>
    [Fact]
    public async Task A_member_can_fetch_content_in_their_own_tenant()
    {
        var acme = await TenantAsync();
        var type = $"xt_{Guid.NewGuid():n}"[..12];
        var acmeContent = await ContentInAsync(acme, type, "acme's own");

        var acmeClient = await MemberAsync(acme, "203.0.113.92");

        var resp = await acmeClient.GetAsync($"/api/contents/{acmeContent}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "isolation that also blocks your own tenant is an outage, not a security property");
    }

    /// <summary>
    /// The list is the path worth the most care.
    /// </summary>
    /// <remarks>
    /// A single fetch fails closed on a missing document. A list that lost its tenant filter returns
    /// everybody's rows at once and still looks like it is working, because the caller sees data.
    /// Both tenants hold content here so an implementation that returned everything would be visible.
    /// </remarks>
    [Fact]
    public async Task A_list_returns_only_the_callers_own_tenants_content()
    {
        var acme = await TenantAsync();
        var globex = await TenantAsync();

        var type = $"xt_{Guid.NewGuid():n}"[..12];
        var needle = $"globex-{Guid.NewGuid():n}";
        await ContentInAsync(globex, type, needle);
        await ContentInAsync(acme, type, "acme's own");

        var acmeClient = await MemberAsync(acme, "203.0.113.93");

        var resp = await acmeClient.GetAsync($"/api/contents?contentType={type}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadAsStringAsync();
        body.Should().NotContain(needle, "a list that forgot its tenant filter returns everybody's rows");
        body.Should().Contain("acme's own", "and the caller still sees their own, or this is an outage");
    }

    /// <summary>
    /// A valid token plus a foreign X-Tenant header does not reach the other tenant's content.
    /// </summary>
    /// <remarks>
    /// The attack the other two tests do not describe, and the one the join actually has to survive.
    ///
    /// `TenantResolutionMiddleware` sets the tenant from the `X-Tenant` header unconditionally, with
    /// no comparison against the token's own tenant claim. `CrossTenantTokenTests` proves a user
    /// cannot mint a token for a tenant they do not belong to, which is a different question: this is
    /// an already-valid token, re-pointed by a header the caller controls.
    ///
    /// It does not leak, and it is worth recording what stops it rather than only that something
    /// does. `TenantAccessMiddleware` compares the token's `tenant` claim against the resolved slug
    /// and answers 403 when they differ. That guard is the entire join, it is one `if`, and nothing
    /// was asserting it: both existing suites pass in full with it deleted, because one never issues
    /// an HTTP request and the other never reads content.
    ///
    /// The assertion is written on the outcome rather than on the 403, so a deployment that made
    /// this a 404 instead would still satisfy it. What must never happen is the content coming back.
    /// </remarks>
    [Fact]
    public async Task A_foreign_tenant_header_on_a_valid_token_does_not_reach_the_other_tenant()
    {
        var acme = await TenantAsync();
        var globex = await TenantAsync();

        var type = $"xt_{Guid.NewGuid():n}"[..12];
        var needle = $"globex-{Guid.NewGuid():n}";
        var globexContent = await ContentInAsync(globex, type, needle);

        var acmeClient = await MemberAsync(acme, "203.0.113.94");

        // The caller keeps their own token and simply asks for somebody else's tenant.
        acmeClient.DefaultRequestHeaders.Remove("X-Tenant");
        acmeClient.DefaultRequestHeaders.Add("X-Tenant", globex);

        var byId = await acmeClient.GetAsync($"/api/contents/{globexContent}");
        var list = await acmeClient.GetAsync($"/api/contents?contentType={type}");

        byId.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests,
            "a rate-limited response proves nothing about isolation");

        var leaked = byId.IsSuccessStatusCode
            || (list.IsSuccessStatusCode && (await list.Content.ReadAsStringAsync()).Contains(needle));

        leaked.Should().BeFalse(
            "membership is what grants a tenant, and a header the caller sets is not membership. "
          + "byId was {0}, list was {1}", byId.StatusCode, list.StatusCode);
    }
}
