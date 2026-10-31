using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using barakoCMS.Infrastructure.Multitenancy;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Features.Tenants;

/// <summary>
/// <c>POST /api/tenants/{handle}/domains/{domain}/verify</c> moves a pending domain to verified when
/// the DNS check passes, and only then (#1280). DNS is faked.
/// </summary>
[Collection("Integration1")]
public class TenantDomainVerifyTests
{
    private const string Edge = "edge.example.net";

    private readonly IntegrationTestFixture _fixture;

    public TenantDomainVerifyTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static int _ip;

    private sealed class FakeDns : IDomainDnsChecker
    {
        public DomainDnsResult Result { get; set; } = new(true, true);

        public List<(string Domain, string Target, string TxtName, string TxtValue)> Calls { get; } = [];

        public Task<DomainDnsResult> CheckAsync(string domain, string cnameTarget, string txtName, string txtValue, CancellationToken ct)
        {
            Calls.Add((domain, cnameTarget, txtName, txtValue));
            return Task.FromResult(Result);
        }
    }

    private (WebApplicationFactory<Program> Host, FakeDns Dns) HostWith(string? deploymentHost = Edge)
    {
        var dns = new FakeDns();
        var host = _fixture.WithWebHostBuilder(b =>
        {
            if (deploymentHost is not null)
                b.UseSetting(TenantDomains.DeploymentHostKey, deploymentHost);
            b.ConfigureServices(s => s.AddSingleton<IDomainDnsChecker>(dns));
        });
        return (host, dns);
    }

    private async Task<HttpClient> SuperAdminAsync(WebApplicationFactory<Program> host)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await _fixture.StoredUserTokenAsync("SuperAdmin"));
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, $"2001:db8:1280:{Interlocked.Increment(ref _ip):x}::1");
        return client;
    }

    private static string Handle() => $"ver-{Guid.NewGuid():n}"[..16];

    private static string Domain() => $"site-{Guid.NewGuid():n}"[..17] + ".example";

    private static Task<HttpResponseMessage> CreateAsync(HttpClient client, string handle, params string[] domains) =>
        client.PostAsJsonAsync("/api/tenants", new { handle, name = handle, isActive = true, domains }, Ct);

    private static Task<HttpResponseMessage> VerifyAsync(HttpClient client, string handle, string domain) =>
        client.PostAsync($"/api/tenants/{handle}/domains/{domain}/verify", null, Ct);

    private static async Task<HttpResponseMessage> ByHostAsync(WebApplicationFactory<Program> host, string domain) =>
        await host.CreateClient().GetAsync($"/api/tenants/by-host/{domain}", Ct);

    private async Task<TenantDomain?> RowAsync(string domain)
    {
        using var scope = _fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IQuerySession>().LoadAsync<TenantDomain>(domain, Ct);
    }

    [Fact]
    public async Task A_passing_check_moves_a_pending_domain_to_verified_and_it_starts_to_route()
    {
        var (host, dns) = HostWith();
        var client = await SuperAdminAsync(host);
        var handle = Handle();
        var domain = Domain();
        (await CreateAsync(client, handle, domain)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await ByHostAsync(host, domain)).StatusCode.Should().Be(HttpStatusCode.NotFound, "it is pending");

        var response = await VerifyAsync(client, handle, domain);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("status").GetString().Should().Be("Verified");
        (await ByHostAsync(host, domain)).StatusCode.Should().Be(HttpStatusCode.OK, "the verified domain routes on the next request");
        var row = await RowAsync(domain);
        row!.Status.Should().Be(TenantDomainStatus.Verified);
        row.VerifiedAt.Should().NotBeNull();

        var call = dns.Calls.Should().ContainSingle().Subject;
        call.Domain.Should().Be(domain);
        call.Target.Should().Be(Edge);
        call.TxtName.Should().Be($"_barako-challenge.{domain}");
        call.TxtValue.Should().Be($"barako-verify={row.Token}", "the check looks for the claim's own token");
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task A_check_that_misses_a_record_leaves_the_domain_pending_and_says_which(bool cname, bool txt)
    {
        var (host, dns) = HostWith();
        dns.Result = new DomainDnsResult(cname, txt);
        var client = await SuperAdminAsync(host);
        var handle = Handle();
        var domain = Domain();
        (await CreateAsync(client, handle, domain)).StatusCode.Should().Be(HttpStatusCode.OK);

        var response = await VerifyAsync(client, handle, domain);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("status").GetString().Should().Be("Pending");
        body.GetProperty("cnameFound").GetBoolean().Should().Be(cname);
        body.GetProperty("txtFound").GetBoolean().Should().Be(txt);
        (await RowAsync(domain))!.Status.Should().Be(TenantDomainStatus.Pending);
        (await ByHostAsync(host, domain)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_check_that_timed_out_is_reported_and_verifies_nothing()
    {
        var (host, dns) = HostWith();
        dns.Result = new DomainDnsResult(false, false, TimedOut: true);
        var client = await SuperAdminAsync(host);
        var handle = Handle();
        var domain = Domain();
        (await CreateAsync(client, handle, domain)).StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await (await VerifyAsync(client, handle, domain)).Content.ReadFromJsonAsync<JsonElement>(Ct);

        body.GetProperty("timedOut").GetBoolean().Should().BeTrue();
        (await RowAsync(domain))!.Status.Should().Be(TenantDomainStatus.Pending);
    }

    [Fact]
    public async Task A_domain_of_another_tenant_or_one_nobody_claimed_is_a_404_and_runs_no_check()
    {
        var (host, dns) = HostWith();
        var client = await SuperAdminAsync(host);
        var owner = Handle();
        var other = Handle();
        var domain = Domain();
        (await CreateAsync(client, owner, domain)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await CreateAsync(client, other)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await VerifyAsync(client, other, domain)).StatusCode.Should().Be(HttpStatusCode.NotFound, "the domain is the owner's");
        (await VerifyAsync(client, owner, Domain())).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await VerifyAsync(client, Handle(), domain)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        dns.Calls.Should().BeEmpty();
        (await RowAsync(domain))!.Status.Should().Be(TenantDomainStatus.Pending);
    }

    [Fact]
    public async Task Without_a_deployment_host_there_is_nothing_to_check_against()
    {
        var (host, dns) = HostWith(deploymentHost: null);
        var client = await SuperAdminAsync(host);
        var handle = Handle();
        var domain = Domain();
        (await CreateAsync(client, handle, domain)).StatusCode.Should().Be(HttpStatusCode.OK);

        var response = await VerifyAsync(client, handle, domain);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        dns.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task The_route_needs_a_signed_in_tenant_manager()
    {
        var (host, _) = HostWith();
        var anonymous = host.CreateClient();
        anonymous.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, $"2001:db8:1280:{Interlocked.Increment(ref _ip):x}::1");

        var response = await VerifyAsync(anonymous, "any-tenant", "any.example");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_deployments_own_host_and_names_under_it_cannot_be_claimed()
    {
        var (host, _) = HostWith();
        var client = await SuperAdminAsync(host);

        foreach (var claimed in new[] { Edge, $"www.{Edge}", $"acme.{Edge}", $"a.b.{Edge}".ToUpperInvariant() })
        {
            var response = await CreateAsync(client, Handle(), claimed);
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, "{0} is the platform's own name", claimed);
        }

        (await CreateAsync(client, Handle(), $"not{Edge}")).StatusCode.Should().Be(HttpStatusCode.OK, "only a real subdomain is under the host");
    }

    /// <summary>On a host of its own so the emptied bucket cannot refuse another test.</summary>
    [Fact]
    public async Task By_host_refuses_the_sixty_first_lookup_in_a_minute_from_one_address()
    {
        using var resolver = FastEndpointsResolver.Keep();
        var (host, _) = HostWith();
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, $"2001:db8:1281:{Interlocked.Increment(ref _ip):x}::1");

        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 61; i++)
        {
            using var response = await client.GetAsync($"/api/tenants/by-host/{Domain()}", Ct);
            codes.Add(response.StatusCode);
        }

        codes.Take(60).Should().OnlyContain(c => c == HttpStatusCode.NotFound);
        codes[60].Should().Be(HttpStatusCode.TooManyRequests);
    }
}
