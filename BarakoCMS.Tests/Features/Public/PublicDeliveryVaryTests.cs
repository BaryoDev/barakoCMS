using Xunit;
using FluentAssertions;
using barakoCMS.Models;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace BarakoCMS.Tests.Features.Public;

/// <summary>
/// Public delivery is cacheable, and the tenant can be resolved from the <c>X-Tenant</c> header
/// (<c>TenantResolutionMiddleware</c>) rather than the host. A shared cache keyed on URL alone would
/// then serve one tenant's response to another, on any deployment routed by that header (or by a
/// path the front end has already reduced to the header by the time a cache sees it). See #546.
///
/// <c>Vary: X-Tenant</c> is how a conforming cache is told the response depends on the header, so
/// these requests build the exact case the fix is for: two tenants, the identical URL, and nothing
/// but <c>X-Tenant</c> telling them apart.
/// </summary>
[Collection("Sequential")]
public class PublicDeliveryVaryTests
{
    private readonly IntegrationTestFixture _factory;
    private readonly HttpClient _anon;

    public PublicDeliveryVaryTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _anon = factory.CreateClient();
    }

    private async Task<string> TenantAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var slug = $"vary-{Guid.NewGuid():N}"[..14].ToLowerInvariant();
        session.Store(new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = slug, IsActive = true });
        await session.SaveChangesAsync();
        return slug;
    }

    /// <summary>Seeds an opted-in type and one Published, Public entry, in one tenant's partition.</summary>
    private async Task SeedAsync(string tenantSlug, string type, string marker)
    {
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession(tenantSlug);

        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = type,
            DisplayName = "Probe",
            IsPubliclyDeliverable = true,
            Fields = new List<FieldDefinition>
            {
                new() { Name = "Title", Type = "string", Sensitivity = SensitivityLevel.Public },
            },
        });
        session.Store(new Content
        {
            Id = Guid.NewGuid(),
            ContentType = type,
            Status = ContentStatus.Published,
            Sensitivity = SensitivityLevel.Public,
            Data = new Dictionary<string, object> { ["Title"] = marker },
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await session.SaveChangesAsync();
    }

    private async Task<HttpResponseMessage> GetAsync(string url, string tenantSlug)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("X-Tenant", tenantSlug);
        return await _anon.SendAsync(req);
    }

    /// <summary>
    /// The scenario the defect describes: one URL, two tenants, the header is the only thing that
    /// tells their responses apart. A cache that ignored <c>Vary: X-Tenant</c> here would hand
    /// tenant B tenant A's content on the next request for the same URL.
    /// </summary>
    [Fact]
    public async Task Two_tenants_share_a_url_and_only_the_tenant_header_names_which_content_comes_back()
    {
        var tenantA = await TenantAsync();
        var tenantB = await TenantAsync();

        // Same content-type name in both tenants, so the URL below is byte-for-byte identical for
        // both requests. Only the resolved tenant, from X-Tenant, decides which entry comes back.
        var type = $"probe{Guid.NewGuid():N}"[..12];
        var markerA = $"MARKER-A-{Guid.NewGuid():N}";
        var markerB = $"MARKER-B-{Guid.NewGuid():N}";
        await SeedAsync(tenantA, type, markerA);
        await SeedAsync(tenantB, type, markerB);

        var url = $"/api/public/{type}";

        var respA = await GetAsync(url, tenantA);
        var respB = await GetAsync(url, tenantB);

        var bodyA = await respA.Content.ReadAsStringAsync();
        var bodyB = await respB.Content.ReadAsStringAsync();

        bodyA.Should().Contain(markerA).And.NotContain(markerB,
            "tenant A's request must return tenant A's content, not tenant B's");
        bodyB.Should().Contain(markerB).And.NotContain(markerA,
            "tenant B's request must return tenant B's content, not tenant A's, even though the URL " +
            "is identical to tenant A's request");

        // Assert the value, not just that the header exists: a Vary that named some other header, or
        // one that dropped X-Tenant from a multi-value list, would tell a cache nothing about the
        // dimension that actually distinguished bodyA from bodyB above.
        respA.Headers.Vary.Should().ContainSingle().Which.Should().Be("X-Tenant",
            "a conforming cache is only told the response depends on the tenant if Vary says so");
        respB.Headers.Vary.Should().ContainSingle().Which.Should().Be("X-Tenant");
    }

    /// <summary>
    /// Every route that shares <c>PublicDelivery.SetCache</c> names the tenant header in <c>Vary</c>,
    /// not just the list route the test above covers. Search, <c>feed.xml</c> and <c>sitemap.xml</c>
    /// were unchecked: a future change that isolated one of them from the shared call site would have
    /// gone undetected. It is the same assertion five times over, which is the point of the routes
    /// sharing one call site in the first place.
    /// </summary>
    [Fact]
    public async Task Every_route_that_shares_SetCache_names_the_tenant_header_in_Vary()
    {
        var tenant = await TenantAsync();
        var type = $"probe{Guid.NewGuid():N}"[..12];
        var marker = $"MARKER-{Guid.NewGuid():N}";
        var slug = "the-entry";

        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        await using (var session = store.LightweightSession(tenant))
        {
            session.Store(new ContentTypeDefinition
            {
                Id = Guid.NewGuid(),
                Name = type,
                DisplayName = "Probe",
                IsPubliclyDeliverable = true,
                Fields = new List<FieldDefinition>
                {
                    new() { Name = "Title", Type = "string", Sensitivity = SensitivityLevel.Public },
                    new() { Name = "Slug", Type = "slug", Sensitivity = SensitivityLevel.Public },
                },
            });
            session.Store(new Content
            {
                Id = Guid.NewGuid(),
                ContentType = type,
                Status = ContentStatus.Published,
                Sensitivity = SensitivityLevel.Public,
                Data = new Dictionary<string, object> { ["Title"] = marker, ["Slug"] = slug },
                SearchText = marker,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await session.SaveChangesAsync();
        }

        var urls = new[]
        {
            $"/api/public/{type}",
            $"/api/public/{type}/{slug}",
            $"/api/public/{type}/search?q={marker}",
            $"/api/public/{type}/search?q=a", // the short-query branch, which used to skip SetCache entirely
            $"/api/public/{type}/feed.xml",
            "/api/public/sitemap.xml",
        };

        foreach (var url in urls)
        {
            var resp = await GetAsync(url, tenant);
            resp.IsSuccessStatusCode.Should().BeTrue(
                $"{url} must succeed to prove anything about its Vary header, got {resp.StatusCode}: " +
                $"{await resp.Content.ReadAsStringAsync()}");
            resp.Headers.Vary.Should().ContainSingle().Which.Should().Be("X-Tenant",
                $"{url} must vary on the tenant header the same way the list route does");
        }
    }
}
