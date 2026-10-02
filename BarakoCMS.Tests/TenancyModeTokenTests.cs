using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// With <c>Tenancy:Mode</c> set to Multi, nothing that carries a tenant is issued or accepted for
/// the default partition or for a slug with no active tenant: not a token, on any path through the
/// issuer, and not an API key (#895).
/// </summary>
/// <remarks>
/// The issuer is asked directly as well as through HTTP. Resolution refuses most of these requests
/// before sign-in runs, so an HTTP test alone could pass on resolution and say nothing about the
/// issuer. <c>POST /api/me/switch</c> is the path where the issuer is the only check, because the
/// target comes from the body and the request itself is on a registered tenant.
/// </remarks>
[Collection("Sequential")]
public class TenancyModeTokenTests
{
    private readonly IntegrationTestFixture _fixture;
    private readonly WebApplicationFactory<Program> _multiHost;
    private readonly HttpClient _multi;

    public TenancyModeTokenTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        _multiHost = MultiTenancyHost.For(fixture);
        _multi = _multiHost.CreateClient();
    }

    private static async Task<TokenIssueResult> IssueAsync(IServiceProvider host, User user, string tenant)
    {
        using var scope = host.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ITokenIssuer>()
            .IssueAccessTokenAsync(user, tenant, ct: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task The_issuer_refuses_the_default_partition_in_Multi()
    {
        var user = await MultiTenancyHost.CreateUserAsync(_fixture);

        var issued = await IssueAsync(_multiHost.Services, user, Tenant.DefaultSlug);

        issued.Allowed.Should().BeFalse("in Multi the default partition is not a tenant anyone can hold a token for");
        issued.Token.Should().BeEmpty();
    }

    [Fact]
    public async Task The_issuer_refuses_a_slug_with_no_tenant_document_in_Multi()
    {
        var user = await MultiTenancyHost.CreateUserAsync(_fixture);

        var issued = await IssueAsync(_multiHost.Services, user, MultiTenancyHost.UnregisteredSlug());

        issued.Allowed.Should().BeFalse("in Multi a slug nobody registered is not a tenant");
        issued.Token.Should().BeEmpty();
    }

    [Fact]
    public async Task The_issuer_gives_an_inactive_tenant_and_an_unregistered_slug_the_same_reason_in_Multi()
    {
        var user = await MultiTenancyHost.CreateUserAsync(_fixture);
        var inactive = await MultiTenancyHost.RegisterTenantAsync(_fixture, active: false);
        await MultiTenancyHost.GrantMembershipAsync(_fixture, user.Id, inactive);

        var forInactive = await IssueAsync(_multiHost.Services, user, inactive);
        var forUnregistered = await IssueAsync(_multiHost.Services, user, MultiTenancyHost.UnregisteredSlug());

        forInactive.Allowed.Should().BeFalse();
        forUnregistered.Allowed.Should().BeFalse();
        forInactive.DenialReason.Should().NotBeNullOrEmpty();
        forInactive.DenialReason.Should().Be(forUnregistered.DenialReason,
            "the reason is logged, and one reason for both keeps a log line from saying which slugs exist");
    }

    /// <summary>The control: an issuer that refused everyone in Multi would pass the three above.</summary>
    [Fact]
    public async Task The_issuer_still_issues_for_a_registered_tenant_the_user_belongs_to_in_Multi()
    {
        var user = await MultiTenancyHost.CreateUserAsync(_fixture);
        var tenant = await MultiTenancyHost.RegisterTenantAsync(_fixture);
        await MultiTenancyHost.GrantMembershipAsync(_fixture, user.Id, tenant);

        var member = await IssueAsync(_multiHost.Services, user, tenant);
        member.Allowed.Should().BeTrue("a member of a registered, active tenant is who Multi is for");

        var stranger = await MultiTenancyHost.CreateUserAsync(_fixture);
        (await IssueAsync(_multiHost.Services, stranger, tenant)).Allowed.Should().BeFalse(
            "membership is still required, as it is in Single");
    }

    /// <summary>The default: with no setting both exemptions hold, as they did before the setting existed.</summary>
    [Fact]
    public async Task With_no_setting_the_issuer_issues_for_the_default_partition_and_an_unregistered_slug()
    {
        var user = await MultiTenancyHost.CreateUserAsync(_fixture);

        (await IssueAsync(_fixture.Services, user, Tenant.DefaultSlug)).Allowed.Should().BeTrue();
        (await IssueAsync(_fixture.Services, user, MultiTenancyHost.UnregisteredSlug())).Allowed.Should().BeTrue();
    }

    private static HttpRequestMessage SignIn(User user, string? tenant)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new { user.Username, MultiTenancyHost.Password }),
        };
        request.Headers.Add(TestRemoteIpFilter.Header, MultiTenancyHost.NextIp());
        if (tenant is not null)
            request.Headers.Add(TenantResolutionMiddleware.TenantHeader, tenant);
        return request;
    }

    [Fact]
    public async Task Signing_in_without_a_tenant_or_under_an_unregistered_slug_gets_no_token_in_Multi()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await MultiTenancyHost.CreateUserAsync(_fixture);
        var tenant = await MultiTenancyHost.RegisterTenantAsync(_fixture);
        await MultiTenancyHost.GrantMembershipAsync(_fixture, user.Id, tenant);

        var member = await _multi.SendAsync(SignIn(user, tenant), ct);
        member.StatusCode.Should().Be(HttpStatusCode.OK,
            "the password is right and the user belongs to the tenant, so the two refusals below are about the tenant");

        var noTenant = await _multi.SendAsync(SignIn(user, tenant: null), ct);
        noTenant.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "no token is issued for the default partition");

        var unregistered = await _multi.SendAsync(SignIn(user, MultiTenancyHost.UnregisteredSlug()), ct);
        unregistered.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "no token is issued for a slug nobody registered");
    }

    private HttpRequestMessage Switch(string bearer, string from, string to)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/me/switch")
        {
            Content = JsonContent.Create(new { Club = to }),
        };
        request.Headers.Add(TenantResolutionMiddleware.TenantHeader, from);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return request;
    }

    [Fact]
    public async Task Switching_to_the_default_partition_or_an_unregistered_slug_is_refused_in_Multi()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await MultiTenancyHost.CreateUserAsync(_fixture);
        var home = await MultiTenancyHost.RegisterTenantAsync(_fixture);
        var away = await MultiTenancyHost.RegisterTenantAsync(_fixture);
        await MultiTenancyHost.GrantMembershipAsync(_fixture, user.Id, home);
        await MultiTenancyHost.GrantMembershipAsync(_fixture, user.Id, away);
        var bearer = MultiTenancyHost.TokenFor(_fixture, user, home);

        var toDefault = await _multi.SendAsync(Switch(bearer, home, Tenant.DefaultSlug), ct);
        toDefault.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "the target comes from the body, so only the issuer stands between a member of one tenant and the default partition");

        var toUnregistered = await _multi.SendAsync(Switch(bearer, home, MultiTenancyHost.UnregisteredSlug()), ct);
        toUnregistered.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var toAway = await _multi.SendAsync(Switch(bearer, home, away), ct);
        toAway.StatusCode.Should().Be(HttpStatusCode.OK,
            "the same bearer switches to a tenant it belongs to, so the refusals above are about the target");
    }

    private async Task<string> StoreKeyAsync(Guid ownerId, string tenantSlug)
    {
        var secret = "bcms_" + Guid.NewGuid().ToString("N");
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new ApiKey
        {
            Id = Guid.NewGuid(),
            Name = "tenancy mode",
            KeyHash = ApiKeyService.Hash(secret),
            Prefix = secret[..12],
            UserId = ownerId,
            TenantSlug = tenantSlug,
            Scopes = ["content:read"],
        });
        await session.SaveChangesAsync();
        return secret;
    }

    private static HttpRequestMessage ListContentWithKey(string secret, string tenant)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/contents");
        request.Headers.Add(TenantResolutionMiddleware.TenantHeader, tenant);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        return request;
    }

    /// <summary>
    /// An API key carries its own tenant, which replaces the one resolution checked. So a key made
    /// for the default partition before the switch to Multi, sent with a registered tenant in the
    /// header, would be served from the default partition if only resolution knew the mode.
    /// </summary>
    [Fact]
    public async Task An_API_key_for_the_default_partition_or_an_unregistered_slug_is_refused_in_Multi()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = await MultiTenancyHost.CreateUserAsync(_fixture, barakoCMS.Data.DataSeeder.SuperAdminRoleId);
        var tenant = await MultiTenancyHost.RegisterTenantAsync(_fixture);
        await MultiTenancyHost.GrantMembershipAsync(_fixture, owner.Id, tenant);

        var forTenant = await StoreKeyAsync(owner.Id, tenant);
        var forDefault = await StoreKeyAsync(owner.Id, Tenant.DefaultSlug);
        var forUnregistered = await StoreKeyAsync(owner.Id, MultiTenancyHost.UnregisteredSlug());

        var served = await _multi.SendAsync(ListContentWithKey(forTenant, tenant), ct);
        served.StatusCode.Should().Be(HttpStatusCode.OK,
            $"a key for a registered tenant its owner belongs to works in Multi, so the refusals below are about the key's tenant: {await served.Content.ReadAsStringAsync(ct)}");

        (await _multi.SendAsync(ListContentWithKey(forDefault, tenant), ct)).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized, "a key scoped to the default partition is not a key for any tenant");
        (await _multi.SendAsync(ListContentWithKey(forUnregistered, tenant), ct)).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized, "a key scoped to a slug nobody registered is not a key for any tenant");
    }

    /// <summary>The default: with no setting a key for the default partition works, as ApiKeyIntegrationTests shows at length.</summary>
    [Fact]
    public async Task With_no_setting_an_API_key_for_the_default_partition_still_works()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = await MultiTenancyHost.CreateUserAsync(_fixture, barakoCMS.Data.DataSeeder.SuperAdminRoleId);
        var forDefault = await StoreKeyAsync(owner.Id, Tenant.DefaultSlug);

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/contents");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", forDefault);
        var response = await _fixture.CreateClient().SendAsync(request, ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct));
    }
}
