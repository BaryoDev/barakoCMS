using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Features.Tenants;

/// <summary>
/// What an operator does to serve a second client from one deployment: add a tenant with its own
/// domain, verify the domain, and the client's users sign in on that domain and see only their own
/// data. The pieces are tested apart; this walks them through the full HTTP pipeline.
/// </summary>
/// <remarks>
/// The host runs in Multi mode, as a deployment that serves a second client must: a request or sign
/// in that resolves to no registered tenant is refused instead of served from the default partition.
/// The DNS check is faked. Each request carries the domain as its <c>Host</c> (the absolute request
/// URI sets it) and no <c>X-Tenant</c>, the way a browser on the client's domain sends it.
///
/// Platform administration is reached from inside a registered tenant, so tenant A is stored first
/// and the admin calls name it with <c>X-Tenant</c>. A token for tenant A on B's domain is covered by
/// <see cref="HostTenantRoutingTests.A_token_for_A_is_refused_on_Bs_domain_with_and_without_X_Tenant_A"/>
/// and is not repeated here.
/// </remarks>
[Collection("Integration1")]
public class SecondTenantOwnDomainTests
{
    private const string Password = "P@ssword123!";
    private const string Edge = "edge.example.net";

    private static int _ip;

    private readonly IntegrationTestFixture _fixture;
    private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _host;
    private readonly HttpClient _client;
    private readonly string _clientIp = $"198.19.{Interlocked.Increment(ref _ip) % 250}.{Random.Shared.Next(1, 250)}";

    public SecondTenantOwnDomainTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        _host = fixture.WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [TenancyOptions.ModeKey] = "Multi",
                [TenantDomains.DeploymentHostKey] = Edge,
            }));
            b.ConfigureServices(s =>
            {
                s.AddSingleton<IStartupFilter, TestRemoteIpFilter>();
                s.AddSingleton<IDomainDnsChecker>(new PassingDns());
            });
        });
        _client = _host.CreateClient();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class PassingDns : IDomainDnsChecker
    {
        public Task<DomainDnsResult> CheckAsync(string domain, string cnameTarget, string txtName, string txtValue, CancellationToken ct) =>
            Task.FromResult(new DomainDnsResult(true, true));
    }

    /// <summary>Two tenants, the second with a pending domain, a user in each, and an admin inside A.</summary>
    private sealed record Setup(string A, string B, string Domain, string UserA, string UserB, string AdminToken);

    private static string Handle(string prefix) => $"{prefix}-{Guid.NewGuid():n}"[..16];

    private static string NewDomain() => $"site-{Guid.NewGuid():n}"[..17] + ".example";

    private async Task<Setup> SetUpAsync()
    {
        var a = Handle("harbor");
        var b = Handle("orchard");
        var domain = NewDomain();

        // Tenant A is stored directly, since in Multi a tenant is reached from inside another one.
        // The host caches the active tenants, and a write on the fixture's own host does not clear it.
        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new Tenant { Id = Guid.NewGuid(), Slug = a, Name = a });
            await session.SaveChangesAsync(Ct);
        }

        _host.Services.GetRequiredService<ITenantDomainSource>().Invalidate();

        var admin = await AdminUserAsync(a);
        string token;
        using (var scope = _host.Services.CreateScope())
        {
            var issued = await scope.ServiceProvider.GetRequiredService<barakoCMS.Infrastructure.Auth.ITokenIssuer>()
                .IssueAccessTokenAsync(admin, a, ct: Ct);
            issued.Allowed.Should().BeTrue("a member of a registered, active tenant signs in to it in Multi");
            token = issued.Token;
        }

        var created = await AdminAsync(HttpMethod.Post, "/api/tenants", new { handle = b, name = b, isActive = true, domains = new[] { domain } }, token, a);
        created.StatusCode.Should().Be(HttpStatusCode.OK, await created.Content.ReadAsStringAsync(Ct));

        return new Setup(a, b, domain, await UserAsync(a), await UserAsync(b), token);
    }

    /// <summary>A platform administrator who belongs to tenant A.</summary>
    private async Task<User> AdminUserAsync(string tenant)
    {
        var id = Guid.NewGuid();
        var user = new User
        {
            Id = id,
            Username = $"own-admin-{id:N}",
            Email = $"own-admin-{id:N}@test.com",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(Password),
            RoleIds = [SystemRoles.SuperAdminRoleId],
        };
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(user);
        session.Store(new Membership
        {
            Id = Guid.NewGuid(),
            UserId = id,
            TenantSlug = tenant,
            Status = MembershipStatus.Active,
            RoleIds = [],
        });
        await session.SaveChangesAsync(Ct);
        return user;
    }

    private async Task<HttpResponseMessage> AdminAsync(HttpMethod method, string path, object? body, string token, string tenant)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add(TestRemoteIpFilter.Header, _clientIp);
        request.Headers.Add(TenantResolutionMiddleware.TenantHeader, tenant);
        return await _client.SendAsync(request, Ct);
    }

    private async Task VerifyAsync(Setup s)
    {
        var response = await AdminAsync(HttpMethod.Post, $"/api/tenants/{s.B}/domains/{s.Domain}/verify", null, s.AdminToken, s.A);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("status").GetString()
            .Should().Be("Verified", "the faked DNS check passes, so the domain starts to route");
    }

    /// <summary>A user with an active membership in one tenant only.</summary>
    private async Task<string> UserAsync(string tenant)
    {
        var username = $"own-{Guid.NewGuid():N}";
        var id = Guid.NewGuid();
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new User
        {
            Id = id,
            Username = username,
            Email = $"{username}@test.com",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(Password),
            RoleIds = [SystemRoles.SuperAdminRoleId],
        });
        session.Store(new Membership
        {
            Id = Guid.NewGuid(),
            UserId = id,
            TenantSlug = tenant,
            Status = MembershipStatus.Active,
            RoleIds = [SystemRoles.SuperAdminRoleId],
        });
        await session.SaveChangesAsync(Ct);
        return username;
    }

    /// <summary>Signs in on <paramref name="host"/>, naming no tenant unless <paramref name="xTenant"/> is set.</summary>
    private async Task<HttpResponseMessage> SignInAsync(string username, string? host, string? xTenant = null)
    {
        var uri = host is null ? "/api/auth/login" : $"http://{host}/api/auth/login";
        var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = JsonContent.Create(new { Username = username, Password }),
        };
        request.Headers.Add(TestRemoteIpFilter.Header, _clientIp);
        if (xTenant is not null)
        {
            request.Headers.Add(TenantResolutionMiddleware.TenantHeader, xTenant);
        }
        return await _client.SendAsync(request, Ct);
    }

    private static async Task<string> TokenAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<barakoCMS.Features.Auth.Login.Response>(Ct))!.Token!;
    }

    private static string? TenantClaimOf(string jwt) =>
        new JwtSecurityTokenHandler().ReadJwtToken(jwt).Claims.FirstOrDefault(c => c.Type == "tenant")?.Value;

    private async Task<HttpResponseMessage> GetAsync(string host, string path, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"http://{host}{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add(TestRemoteIpFilter.Header, _clientIp);
        return await _client.SendAsync(request, Ct);
    }

    /// <summary>Stores an entry in one tenant's partition, the way the API would.</summary>
    private async Task<Guid> EntryInAsync(string tenant, string type, string title)
    {
        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession(tenant);

        var id = Guid.NewGuid();
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = type,
            DisplayName = type,
            Fields = [new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string" }],
        });
        session.Store(new Content
        {
            Id = id,
            ContentType = type,
            Status = ContentStatus.Published,
            Sensitivity = SensitivityLevel.Public,
            Data = new Dictionary<string, object> { ["Title"] = title },
        });
        await session.SaveChangesAsync(Ct);
        return id;
    }

    private Task<HttpResponseMessage> AskAsync(string domain)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/tenants/tls-ask?domain={Uri.EscapeDataString(domain)}");
        request.Headers.Add(TestRemoteIpFilter.Header, _clientIp);
        return _client.SendAsync(request, Ct);
    }

    [Fact]
    public async Task Before_it_is_verified_a_sign_in_on_the_domain_is_refused_and_after_it_the_token_is_B()
    {
        var s = await SetUpAsync();

        var onPending = await SignInAsync(s.UserB, s.Domain);

        // Sign in is a tenantless route, so resolution does not answer 404 here. A pending domain is
        // nobody's, the request runs on the default partition, and the issuer refuses it in Multi.
        onPending.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests, "a rate limited answer proves nothing");
        onPending.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "a pending domain does not route to B, and in Multi the default partition gets no token");
        (await onPending.Content.ReadAsStringAsync(Ct)).Should().NotContain("\"token\"", "an unverified claim must not act as tenant B");

        await VerifyAsync(s);

        TenantClaimOf(await TokenAsync(await SignInAsync(s.UserB, s.Domain))).Should().Be(s.B,
            "the same sign in lands on B once the domain is verified");
    }

    [Fact]
    public async Task A_user_of_B_signing_in_on_the_deployment_host_with_no_X_Tenant_is_refused()
    {
        var s = await SetUpAsync();
        await VerifyAsync(s);

        var onEdge = await SignInAsync(s.UserB, Edge);

        onEdge.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests, "a rate limited answer proves nothing");
        onEdge.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "the deployment host resolves to no registered tenant, so the sign in runs on the default partition, which gets no token in Multi");
        (await onEdge.Content.ReadAsStringAsync(Ct)).Should().NotContain("\"token\"");

        TenantClaimOf(await TokenAsync(await SignInAsync(s.UserB, Edge, s.B))).Should().Be(s.B,
            "the control: naming B with X-Tenant on the same host signs in, so the refusal is about naming no tenant");
    }

    [Fact]
    public async Task A_user_of_B_signs_in_on_Bs_own_domain_with_no_X_Tenant_and_gets_a_token_for_B()
    {
        var s = await SetUpAsync();
        await VerifyAsync(s);

        var response = await SignInAsync(s.UserB, s.Domain);

        TenantClaimOf(await TokenAsync(response)).Should().Be(s.B,
            "the verified domain names the tenant, so the client sends no X-Tenant and the token is B's");
    }

    [Fact]
    public async Task On_Bs_domain_the_list_holds_only_Bs_entries_and_As_entry_is_not_found()
    {
        var s = await SetUpAsync();
        await VerifyAsync(s);
        var type = $"own_{Guid.NewGuid():n}"[..12];
        var titleA = $"harbor-{Guid.NewGuid():n}";
        var titleB = $"orchard-{Guid.NewGuid():n}";
        var entryA = await EntryInAsync(s.A, type, titleA);
        var entryB = await EntryInAsync(s.B, type, titleB);
        var token = await TokenAsync(await SignInAsync(s.UserB, s.Domain));

        var inA = new HttpRequestMessage(HttpMethod.Get, $"/api/contents/{entryA}");
        inA.Headers.Authorization = new AuthenticationHeaderValue("Bearer", s.AdminToken);
        inA.Headers.Add(TestRemoteIpFilter.Header, _clientIp);
        inA.Headers.Add(TenantResolutionMiddleware.TenantHeader, s.A);
        var readInA = await _client.SendAsync(inA, Ct);
        readInA.StatusCode.Should().Be(HttpStatusCode.OK,
            "the control: A's entry exists in A, so the not found below is isolation and not an entry that was never stored");
        (await readInA.Content.ReadAsStringAsync(Ct)).Should().Contain(titleA);

        var list = await GetAsync(s.Domain, $"/api/contents?contentType={type}", token);

        list.StatusCode.Should().Be(HttpStatusCode.OK, await list.Content.ReadAsStringAsync(Ct));
        var body = await list.Content.ReadAsStringAsync(Ct);
        body.Should().Contain(titleB, "B's own entry of the type is listed, or this is an outage and not isolation");
        body.Should().NotContain(titleA, "A holds an entry of the same type, and the list is read from B's partition");

        (await GetAsync(s.Domain, $"/api/contents/{entryB}", token)).StatusCode.Should().Be(HttpStatusCode.OK,
            "the control: B's own entry is fetched by id on B's domain");
        (await GetAsync(s.Domain, $"/api/contents/{entryA}", token)).StatusCode.Should().Be(HttpStatusCode.NotFound,
            "A's entry id is real, but B's session cannot see another partition, so it is not found");
    }

    [Fact]
    public async Task A_user_of_A_only_gets_no_token_on_Bs_domain()
    {
        var s = await SetUpAsync();
        await VerifyAsync(s);

        (await TokenAsync(await SignInAsync(s.UserA, null, s.A))).Should().NotBeNullOrEmpty(
            "the control: the same credentials sign in on A's own route");
        TenantClaimOf(await TokenAsync(await SignInAsync(s.UserB, s.Domain))).Should().Be(s.B,
            "the control on the same path: B's user signs in on B's domain, so the domain routes to B and the refusal below is about membership");

        var onB = await SignInAsync(s.UserA, s.Domain);

        onB.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests, "a rate limited answer proves nothing about membership");
        onB.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "B's domain resolves to B and this user has no membership in B, so the answer is the same as for a wrong password");
        (await onB.Content.ReadAsStringAsync(Ct)).Should().NotContain("\"token\"", "no token for A or B comes back");
    }

    [Fact]
    public async Task The_TLS_ask_refuses_the_domain_while_it_is_pending_and_allows_it_once_verified()
    {
        var s = await SetUpAsync();

        (await AskAsync(s.Domain)).StatusCode.Should().Be(HttpStatusCode.NotFound,
            "a pending domain is an unproven claim, and a certificate must not be issued for it");

        await VerifyAsync(s);

        (await AskAsync(s.Domain)).StatusCode.Should().Be(HttpStatusCode.OK,
            "a verified domain of an active tenant may get a certificate");
    }
}
