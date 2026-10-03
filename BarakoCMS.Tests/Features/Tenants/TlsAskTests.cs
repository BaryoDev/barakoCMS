using System.Net;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Features.Tenants;

/// <summary>
/// <c>GET /api/tenants/tls-ask?domain=</c>, the endpoint Caddy's on-demand TLS asks before it gets a
/// certificate for a host: yes for a domain an active tenant holds, no for anything else (#904).
/// </summary>
[Collection("Sequential")]
public class TlsAskTests
{
    private readonly IntegrationTestFixture _fixture;

    public TlsAskTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static int _ip;

    private static string Domain() => $"tls-{Guid.NewGuid():n}"[..16] + ".example";

    private static HttpClient From(WebApplicationFactory<Program> host)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, $"2001:db8:904::{Interlocked.Increment(ref _ip):x}");
        return client;
    }

    private async Task<string> StoreTenantAsync(WebApplicationFactory<Program> host, bool active, params string[] domains)
    {
        var slug = $"tls-{Guid.NewGuid():N}";
        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = slug, IsActive = active, Domains = domains.ToList() });
            await session.SaveChangesAsync(Ct);
        }

        host.Services.GetRequiredService<ITenantDomainSource>().Invalidate();
        return slug;
    }

    private static Task<HttpResponseMessage> Ask(HttpClient client, string? domain) =>
        client.GetAsync(domain is null ? "/api/tenants/tls-ask" : $"/api/tenants/tls-ask?domain={Uri.EscapeDataString(domain)}", Ct);

    [Fact]
    public async Task A_registered_domain_is_allowed_and_the_answer_names_no_tenant()
    {
        var domain = Domain();
        var slug = await StoreTenantAsync(_fixture, active: true, domain);
        var client = From(_fixture);

        foreach (var host in new[] { domain, $"www.{domain}" })
        {
            var response = await Ask(client, host);
            response.StatusCode.Should().Be(HttpStatusCode.OK, "{0} is a registered domain", host);
            (await response.Content.ReadAsStringAsync(Ct)).Should().NotContain(slug);
        }
    }

    /// <summary>
    /// The positive control comes first, so a missing route cannot pass for a refusal.
    /// </summary>
    [Fact]
    public async Task Anything_that_is_not_an_active_tenants_domain_is_refused()
    {
        var domain = Domain();
        var inactive = Domain();
        var slug = await StoreTenantAsync(_fixture, active: true, domain);
        await StoreTenantAsync(_fixture, active: false, inactive);
        var client = From(_fixture);

        (await Ask(client, domain)).StatusCode.Should().Be(HttpStatusCode.OK, "the positive control");

        string?[] refused =
        [
            null,
            "",
            Domain(),
            inactive,
            $"{slug}.example.com",
            $"evil.{domain}",
            $"www.www.{domain}",
            $"{domain}:443",
            $"*.{domain}",
            $"{domain}.",
            "203.0.113.9",
            "[2001:db8::1]",
            new string('a', 300) + ".example",
        ];
        refused.Should().HaveCount(13);

        foreach (var name in refused)
        {
            var response = await Ask(client, name);
            response.StatusCode.Should().Be(HttpStatusCode.NotFound, "'{0}' is not a registered domain", name);
            (await response.Content.ReadAsStringAsync(Ct)).Should().NotContain(slug);
        }
    }

    [Fact]
    public async Task In_Multi_the_ask_answers_on_the_deployments_own_host()
    {
        var multi = MultiTenancyHost.For(_fixture);
        var domain = Domain();
        await StoreTenantAsync(multi, active: true, domain);

        var response = await Ask(From(multi), domain);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "the proxy asks on the API's own host, which names no tenant, so the route has to be tenantless");
    }

    [Fact]
    public async Task The_sixty_first_question_from_one_address_in_a_minute_is_refused()
    {
        var client = From(_fixture);
        var unknown = Domain();

        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 61; i++)
        {
            using var response = await Ask(client, unknown);
            codes.Add(response.StatusCode);
        }

        codes.Should().HaveCount(61);
        codes.Take(60).Should().OnlyContain(c => c == HttpStatusCode.NotFound);
        codes[60].Should().Be(HttpStatusCode.TooManyRequests);

        (await Ask(From(_fixture), unknown)).StatusCode.Should().Be(HttpStatusCode.NotFound, "another address has its own bucket");
    }
}
