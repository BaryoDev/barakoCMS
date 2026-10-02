using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// The one host the tenancy mode classes share, with <c>Tenancy:Mode</c> set to Multi, and the
/// data they all need.
/// </summary>
/// <remarks>
/// The host is not disposed, for the reason <see cref="IntegrationTestFixture.WithSetting"/> gives.
///
/// Tenants are written through the fixture's own host, which runs in Single and shares the
/// database. The Multi host caches which slugs are active tenants, and a write on another host
/// does not clear that, so every helper that writes a tenant clears it by hand. A deployment with
/// two instances waits out <c>Multitenancy:CacheDuration</c> instead.
/// </remarks>
internal static class MultiTenancyHost
{
    private static readonly Lock Gate = new();
    private static WebApplicationFactory<Program>? _host;
    private static int _ip;

    public static WebApplicationFactory<Program> For(IntegrationTestFixture fixture)
    {
        lock (Gate)
        {
            return _host ??= fixture.WithSetting(TenancyOptions.ModeKey, "Multi");
        }
    }

    /// <summary>An address no other request used, so the sign-in rate limit counts each one alone.</summary>
    public static string NextIp() => $"192.0.2.{Interlocked.Increment(ref _ip) % 250 + 1}";

    public static string UnregisteredSlug() => $"ghost-{Guid.NewGuid():N}";

    public static async Task<string> RegisterTenantAsync(IntegrationTestFixture fixture, bool active = true)
    {
        var slug = $"tm-{Guid.NewGuid():N}";

        using (var scope = fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = slug, IsActive = active });
            await session.SaveChangesAsync();
        }

        For(fixture).Services.GetRequiredService<ITenantDomainSource>().Invalidate();
        return slug;
    }

    public const string Password = "P@ssword123!";

    public static async Task<User> CreateUserAsync(IntegrationTestFixture fixture, params Guid[] globalRoleIds)
    {
        using var scope = fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var id = Guid.NewGuid();
        var user = new User
        {
            Id = id,
            Username = $"tm-{id:N}",
            Email = $"tm-{id:N}@test.com",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(Password),
            RoleIds = globalRoleIds.ToList(),
        };
        session.Store(user);
        await session.SaveChangesAsync();
        return user;
    }

    public static async Task GrantMembershipAsync(IntegrationTestFixture fixture, Guid userId, string tenantSlug)
    {
        using var scope = fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new Membership
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TenantSlug = tenantSlug,
            Status = MembershipStatus.Active,
            RoleIds = new List<Guid>(),
        });
        await session.SaveChangesAsync();
    }

    /// <summary>A bearer for a stored user, carrying the tenant claim the issuer would have set.</summary>
    public static string TokenFor(IntegrationTestFixture fixture, User user, string tenant, params string[] roles) =>
        fixture.CreateToken(
            roles.Length == 0 ? new[] { "User" } : roles,
            user.Id.ToString(),
            new Dictionary<string, string> { ["Username"] = user.Username, ["tenant"] = tenant });
}

/// <summary>
/// With <c>Tenancy:Mode</c> set to Multi, a request that names no registered, active tenant is
/// refused at resolution, before authentication and before any endpoint (#895).
/// </summary>
/// <remarks>
/// The probe is <c>GET /api/contents</c> with no token. A request that gets as far as the endpoint
/// answers 401, so a 404 carrying <see cref="TenantResolutionMiddleware.NoTenantMessage"/> can only
/// have come from resolution, and a 401 shows the request was let through.
/// </remarks>
[Collection("Sequential")]
public class TenancyModeResolutionTests
{
    private const string Probe = "/api/contents";

    private readonly IntegrationTestFixture _fixture;
    private readonly HttpClient _multi;
    private readonly HttpClient _single;

    public TenancyModeResolutionTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        _multi = MultiTenancyHost.For(fixture).CreateClient();
        _single = fixture.CreateClient();
    }

    private static HttpRequestMessage Get(string path, string? tenant = null, string? host = null)
    {
        // An absolute address is how the test server is given a host: it takes the request's host
        // from the address, and nothing is looked up.
        var request = new HttpRequestMessage(HttpMethod.Get, host is null ? path : $"http://{host}{path}");
        if (tenant is not null)
            request.Headers.Add(TenantResolutionMiddleware.TenantHeader, tenant);
        return request;
    }

    private static async Task ShouldBeTheRefusalAsync(HttpResponseMessage response, string because)
    {
        response.StatusCode.Should().Be(HttpStatusCode.NotFound, because);
        (await response.Content.ReadAsStringAsync()).Should().Be(TenantResolutionMiddleware.NoTenantMessage, because);
    }

    private static async Task ShouldHaveReachedTheEndpointAsync(HttpResponseMessage response, string because)
    {
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, because);
        (await response.Content.ReadAsStringAsync()).Should().NotBe(TenantResolutionMiddleware.NoTenantMessage);
    }

    [Fact]
    public async Task A_request_that_names_no_tenant_is_refused_in_Multi()
    {
        var ct = TestContext.Current.CancellationToken;

        await ShouldHaveReachedTheEndpointAsync(await _single.SendAsync(Get(Probe), ct),
            "with no setting the default partition serves, so the refusal below is the mode's doing");

        await ShouldBeTheRefusalAsync(await _multi.SendAsync(Get(Probe), ct),
            "in Multi the default partition belongs to no tenant");
    }

    [Fact]
    public async Task The_default_slug_named_in_the_header_is_refused_in_Multi()
    {
        var ct = TestContext.Current.CancellationToken;

        await ShouldBeTheRefusalAsync(await _multi.SendAsync(Get(Probe, tenant: Tenant.DefaultSlug), ct),
            "naming the default partition is not naming a tenant");
    }

    [Fact]
    public async Task A_slug_with_no_tenant_document_is_refused_in_Multi_when_named_in_the_header()
    {
        var ct = TestContext.Current.CancellationToken;
        var ghost = MultiTenancyHost.UnregisteredSlug();

        await ShouldHaveReachedTheEndpointAsync(await _single.SendAsync(Get(Probe, tenant: ghost), ct),
            "with no setting an unregistered slug is served as a partition of its own");

        await ShouldBeTheRefusalAsync(await _multi.SendAsync(Get(Probe, tenant: ghost), ct),
            "in Multi a caller cannot make a partition by naming it");
    }

    [Fact]
    public async Task A_slug_with_no_tenant_document_is_refused_in_Multi_when_it_comes_from_the_subdomain()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = $"{MultiTenancyHost.UnregisteredSlug()}.example.com";

        await ShouldHaveReachedTheEndpointAsync(await _single.SendAsync(Get(Probe, host: host), ct),
            "with no setting a subdomain nobody registered is served");

        await ShouldBeTheRefusalAsync(await _multi.SendAsync(Get(Probe, host: host), ct),
            "a mistyped subdomain must not land in a partition nobody owns");
    }

    [Fact]
    public async Task An_inactive_tenant_gets_the_same_answer_as_a_slug_nobody_registered()
    {
        var ct = TestContext.Current.CancellationToken;
        var inactive = await MultiTenancyHost.RegisterTenantAsync(_fixture, active: false);

        var forInactive = await _multi.SendAsync(Get(Probe, tenant: inactive), ct);
        var forUnregistered = await _multi.SendAsync(Get(Probe, tenant: MultiTenancyHost.UnregisteredSlug()), ct);

        await ShouldBeTheRefusalAsync(forInactive, "a tenant switched off is not served");
        forInactive.StatusCode.Should().Be(forUnregistered.StatusCode);
        (await forInactive.Content.ReadAsStringAsync(ct)).Should().Be(
            await forUnregistered.Content.ReadAsStringAsync(ct),
            "a different body would tell a caller which slugs were ever registered");
        forInactive.Content.Headers.ContentType?.ToString().Should().Be(
            forUnregistered.Content.Headers.ContentType?.ToString());
    }

    /// <summary>
    /// The control for the refusals above: without it, a resolution step that refused everything
    /// would pass every one of them.
    /// </summary>
    [Fact]
    public async Task A_registered_active_tenant_is_served_in_Multi_by_header_and_by_subdomain()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenant = await MultiTenancyHost.RegisterTenantAsync(_fixture);

        await ShouldHaveReachedTheEndpointAsync(await _multi.SendAsync(Get(Probe, tenant: tenant), ct),
            "the header names an active tenant");
        await ShouldHaveReachedTheEndpointAsync(await _multi.SendAsync(Get(Probe, host: $"{tenant}.example.com"), ct),
            "the subdomain names an active tenant");
    }

    [Fact]
    public async Task The_routes_that_need_no_tenant_still_answer_in_Multi()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenant = await MultiTenancyHost.RegisterTenantAsync(_fixture);

        var live = await _multi.SendAsync(Get("/health/live"), ct);
        live.StatusCode.Should().Be(HttpStatusCode.OK, "a probe names no tenant");

        var meta = await _multi.SendAsync(Get("/api/meta"), ct);
        await ShouldHaveReachedTheEndpointAsync(meta, "the contract version is read before a tenant is known");

        var byHost = await _multi.SendAsync(Get($"/api/tenants/by-host/{tenant}.example.com"), ct);
        byHost.StatusCode.Should().Be(HttpStatusCode.OK, "a renderer asks this to learn the tenant");
        (await byHost.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("handle").GetString().Should().Be(tenant);

        var profile = await _multi.SendAsync(Get($"/api/tenants/{tenant}/public"), ct);
        profile.StatusCode.Should().Be(HttpStatusCode.OK, "a sign-in page asks this to learn the tenant");
        (await profile.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("handle").GetString().Should().Be(tenant);

        var signIn = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new { Username = $"nobody-{Guid.NewGuid():N}", MultiTenancyHost.Password }),
        };
        signIn.Headers.Add(TestRemoteIpFilter.Header, MultiTenancyHost.NextIp());
        await ShouldHaveReachedTheEndpointAsync(await _multi.SendAsync(signIn, ct),
            "sign-in is reached without a tenant, and it is the issuer that decides what it may issue");
    }

    /// <summary>
    /// The list is an allowlist: a route added tomorrow is refused until somebody adds it, and so
    /// are the real routes that were left off on purpose.
    /// </summary>
    [Theory]
    [InlineData("/api/tenancy-mode-route-added-tomorrow")]
    [InlineData("/api/me/tenants")]
    [InlineData("/api/tenants")]
    [InlineData("/api/modules")]
    [InlineData("/api/public/tenancy-mode-probe")]
    [InlineData("/swagger/index.html")]
    public async Task A_route_that_is_not_listed_is_refused_without_a_tenant_in_Multi(string path)
    {
        var ct = TestContext.Current.CancellationToken;

        var single = await _single.SendAsync(Get(path), ct);
        (await single.Content.ReadAsStringAsync(ct)).Should().NotBe(TenantResolutionMiddleware.NoTenantMessage,
            "with no setting nothing is refused for lack of a tenant");

        await ShouldBeTheRefusalAsync(await _multi.SendAsync(Get(path), ct),
            $"{path} is not on the list of routes that answer without a tenant");
    }

    /// <summary>
    /// A browser sends a preflight without <c>X-Tenant</c>. Refusing it would stop every
    /// cross-origin call that names its tenant in that header.
    /// </summary>
    [Fact]
    public async Task A_preflight_that_names_no_tenant_is_left_to_CORS_in_Multi()
    {
        var ct = TestContext.Current.CancellationToken;
        var preflight = new HttpRequestMessage(HttpMethod.Options, Probe)
        {
            Headers =
            {
                { "Origin", "https://console.example.com" },
                { "Access-Control-Request-Method", "GET" },
            },
        };

        var response = await _multi.SendAsync(preflight, ct);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent, "the CORS middleware answers a preflight itself");
        (await response.Content.ReadAsStringAsync(ct)).Should().NotBe(TenantResolutionMiddleware.NoTenantMessage);
    }

    /// <summary>
    /// Platform administration in Multi: a SuperAdmin who belongs to one registered tenant signs in
    /// there and manages every tenant from it. A tenant made that way is served on the next request.
    /// </summary>
    [Fact]
    public async Task A_SuperAdmin_signed_in_to_a_registered_tenant_lists_and_creates_tenants_in_Multi()
    {
        var ct = TestContext.Current.CancellationToken;
        var home = await MultiTenancyHost.RegisterTenantAsync(_fixture);
        var admin = await MultiTenancyHost.CreateUserAsync(_fixture, barakoCMS.Data.DataSeeder.SuperAdminRoleId);
        await MultiTenancyHost.GrantMembershipAsync(_fixture, admin.Id, home);

        string token;
        using (var scope = MultiTenancyHost.For(_fixture).Services.CreateScope())
        {
            var issued = await scope.ServiceProvider.GetRequiredService<barakoCMS.Infrastructure.Auth.ITokenIssuer>()
                .IssueAccessTokenAsync(admin, home, ct: ct);
            issued.Allowed.Should().BeTrue("a member of a registered, active tenant signs in to it in Multi");
            token = issued.Token;
        }

        var list = Get("/api/tenants", tenant: home);
        list.Headers.Authorization = new("Bearer", token);
        var listed = await _multi.SendAsync(list, ct);
        listed.StatusCode.Should().Be(HttpStatusCode.OK, await listed.Content.ReadAsStringAsync(ct));

        var handle = $"made-{Guid.NewGuid():N}"[..20];
        var create = new HttpRequestMessage(HttpMethod.Post, "/api/tenants")
        {
            Content = JsonContent.Create(new { Handle = handle, Name = "Made in Multi", IsActive = true }),
        };
        create.Headers.Add(TenantResolutionMiddleware.TenantHeader, home);
        create.Headers.Authorization = new("Bearer", token);
        var created = await _multi.SendAsync(create, ct);
        created.StatusCode.Should().Be(HttpStatusCode.OK, await created.Content.ReadAsStringAsync(ct));

        await ShouldHaveReachedTheEndpointAsync(await _multi.SendAsync(Get(Probe, tenant: handle), ct),
            "creating a tenant clears the cached list of active tenants on the instance that created it");

        var withoutTenant = Get("/api/tenants");
        withoutTenant.Headers.Authorization = new("Bearer", token);
        await ShouldBeTheRefusalAsync(await _multi.SendAsync(withoutTenant, ct),
            "tenant administration is reached from a registered tenant, not from the default partition");
    }

    /// <summary>The default: nothing above applies when the setting is absent.</summary>
    [Fact]
    public async Task With_no_setting_the_default_partition_and_an_unregistered_slug_are_served_as_before()
    {
        var ct = TestContext.Current.CancellationToken;

        await ShouldHaveReachedTheEndpointAsync(await _single.SendAsync(Get(Probe), ct), "no tenant named");
        await ShouldHaveReachedTheEndpointAsync(
            await _single.SendAsync(Get(Probe, tenant: MultiTenancyHost.UnregisteredSlug()), ct), "an unregistered slug");
        await ShouldHaveReachedTheEndpointAsync(
            await _single.SendAsync(Get(Probe, tenant: Tenant.DefaultSlug), ct), "the default slug by name");
    }
}
