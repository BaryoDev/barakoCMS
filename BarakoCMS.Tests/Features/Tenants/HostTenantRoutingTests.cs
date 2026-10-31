using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Features.Tenants;

/// <summary>
/// A verified domain belongs to its tenant. A request on it cannot name another tenant with
/// <c>X-Tenant</c>, so a token for A is no use on B's domain, with or without the header (#1280).
/// </summary>
[Collection("Integration1")]
public class HostTenantRoutingTests
{
    private const string Password = "P@ssword123!";

    private static int _ip;

    private readonly IntegrationTestFixture _fixture;
    private readonly HttpClient _client;
    private readonly string _clientIp = $"198.18.{Interlocked.Increment(ref _ip) % 250}.{Random.Shared.Next(1, 250)}";

    public HostTenantRoutingTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        _client = fixture
            .WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IStartupFilter, TestRemoteIpFilter>()))
            .CreateClient();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FixedDomains(TenantDomainMap map) : ITenantDomainSource
    {
        public bool RefuseUnknownHosts => false;

        public Task<TenantDomainMap> GetAsync(CancellationToken ct = default) => Task.FromResult(map);

        public void Invalidate()
        {
        }
    }

    private static async Task<(bool Reached, string Slug, int Status, string Body)> RunAsync(string host, string? header)
    {
        var map = new TenantDomainMap([("b.example.com", "tenant-b")], ["tenant-a", "tenant-b"]);
        var services = new ServiceCollection()
            .AddSingleton(new TenancyOptions())
            .BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Path = "/api/contents";
        context.Request.Host = new HostString(host);
        context.Response.Body = new MemoryStream();
        if (header is not null)
            context.Request.Headers[TenantResolutionMiddleware.TenantHeader] = header;

        var tenant = new TenantContext();
        var reached = false;
        var middleware = new TenantResolutionMiddleware(_ =>
        {
            reached = true;
            return Task.CompletedTask;
        });
        await middleware.InvokeAsync(context, tenant, new FixedDomains(map));

        context.Response.Body.Position = 0;
        return (reached, tenant.Slug, context.Response.StatusCode, await new StreamReader(context.Response.Body).ReadToEndAsync());
    }

    [Fact]
    public async Task On_a_verified_domain_an_X_Tenant_for_another_tenant_is_refused_with_403()
    {
        var outcome = await RunAsync("b.example.com", "tenant-a");

        outcome.Reached.Should().BeFalse("nothing after resolution may run as tenant A on B's domain");
        outcome.Status.Should().Be(StatusCodes.Status403Forbidden);
        outcome.Body.Should().Be(TenantResolutionMiddleware.WrongTenantMessage);
    }

    [Theory]
    [InlineData("tenant-b")]
    [InlineData(" Tenant-B ")]
    [InlineData(null)]
    public async Task On_a_verified_domain_the_same_X_Tenant_or_none_goes_on_as_the_domains_tenant(string? header)
    {
        var outcome = await RunAsync("www.B.example.com", header);

        outcome.Reached.Should().BeTrue();
        outcome.Slug.Should().Be("tenant-b");
    }

    [Fact]
    public async Task On_a_host_that_is_not_a_tenant_domain_X_Tenant_still_names_the_tenant()
    {
        var outcome = await RunAsync("api.example.com", "tenant-a");

        outcome.Reached.Should().BeTrue("path-based routing sets the header from the URL on the shared host");
        outcome.Slug.Should().Be("tenant-a");
    }

    private async Task<string> StoreTenantAsync(string? verifiedDomain)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var slug = $"hostrt-{Guid.NewGuid():N}"[..20];
        session.Store(new Tenant
        {
            Id = Guid.NewGuid(),
            Slug = slug,
            Name = slug,
            Domains = verifiedDomain is null ? [] : [verifiedDomain],
        });
        if (verifiedDomain is not null)
            VerifiedDomains.Stage(session, slug, [verifiedDomain]);
        await session.SaveChangesAsync(Ct);
        scope.ServiceProvider.GetRequiredService<ITenantDomainSource>().Invalidate();
        return slug;
    }

    private async Task<string> TokenForAsync(string tenant)
    {
        var username = $"hostrt-{Guid.NewGuid():N}";
        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var id = Guid.NewGuid();
            session.Store(new User
            {
                Id = id,
                Username = username,
                Email = $"{username}@test.com",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(Password),
            });
            session.Store(new Membership
            {
                Id = Guid.NewGuid(),
                UserId = id,
                TenantSlug = tenant,
                Status = MembershipStatus.Active,
                RoleIds = [],
            });
            await session.SaveChangesAsync(Ct);
        }

        var login = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new { Username = username, Password }),
        };
        login.Headers.Add(TestRemoteIpFilter.Header, _clientIp);
        login.Headers.Add("X-Tenant", tenant);
        var response = await _client.SendAsync(login, Ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<barakoCMS.Features.Auth.Login.Response>(Ct))!.Token!;
    }

    private async Task<HttpResponseMessage> WhoAmIAsync(string token, string? host, string? xTenant)
    {
        var uri = host is null ? "/api/me" : $"http://{host}/api/me";
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add(TestRemoteIpFilter.Header, _clientIp);
        if (xTenant is not null)
            request.Headers.Add("X-Tenant", xTenant);
        return await _client.SendAsync(request, Ct);
    }

    [Fact]
    public async Task A_token_for_A_is_refused_on_Bs_domain_with_and_without_X_Tenant_A()
    {
        var domain = $"b-{Guid.NewGuid():N}"[..14] + ".example.com";
        var a = await StoreTenantAsync(null);
        var b = await StoreTenantAsync(domain);
        var token = await TokenForAsync(a);

        (await WhoAmIAsync(token, null, a)).StatusCode.Should().Be(HttpStatusCode.OK, "the token works on tenant A's own route");

        var named = await WhoAmIAsync(token, domain, a);
        named.StatusCode.Should().Be(HttpStatusCode.Forbidden, "X-Tenant: A must not turn B's domain into A's");
        (await named.Content.ReadAsStringAsync(Ct)).Should().Be(TenantResolutionMiddleware.WrongTenantMessage);

        (await WhoAmIAsync(token, domain, null)).StatusCode.Should().Be(HttpStatusCode.Forbidden, "B's domain resolves to B, and the token is A's");
        (await WhoAmIAsync(token, domain, b)).StatusCode.Should().Be(HttpStatusCode.Forbidden, "naming B does not make the token B's");
    }
}
