using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.Site;

/// <summary>
/// Site mode Private (#858): a tenant's public delivery answers 404 to anyone without a link to the
/// whole site or a signed-in viewer who may read the <c>site</c> type, and other tenants are
/// unaffected.
/// </summary>
[Collection("Integration1")]
public class PrivateSiteModeTests
{
    private readonly IntegrationTestFixture _fixture;
    private readonly ShareLinkTestHost _host;

    public PrivateSiteModeTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        _host = new ShareLinkTestHost(fixture);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task StoreSiteAsync(string tenant, string? mode, ContentStatus status = ContentStatus.Published)
    {
        var data = new Dictionary<string, object> { ["Name"] = "Club" };
        if (mode is not null)
        {
            data["Mode"] = mode;
        }

        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession(tenant);
        session.Store(new Content
        {
            Id = Guid.NewGuid(),
            ContentType = "site",
            Status = status,
            Sensitivity = SensitivityLevel.Public,
            Data = data,
        });
        await session.SaveChangesAsync(Ct);
    }

    private async Task StoreRawSiteAsync(string tenant, Dictionary<string, object> data)
    {
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession(tenant);
        session.Store(new Content
        {
            Id = Guid.NewGuid(),
            ContentType = "site",
            Status = ContentStatus.Published,
            Sensitivity = SensitivityLevel.Public,
            Data = data,
        });
        await session.SaveChangesAsync(Ct);
    }

    private HttpClient AnonymousIn(string tenant, string? key = null)
    {
        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant", tenant);
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, ShareLinkTestHost.NextIp());
        if (key is not null)
        {
            client.DefaultRequestHeaders.Add("X-Share-Key", key);
        }

        return client;
    }

    private async Task<(string Tenant, string Type)> PrivateTenantWithEntryAsync()
    {
        var tenant = await _host.TenantAsync();
        var type = await _host.TypeAsync(tenant);
        await _host.EntryAsync(tenant, type, "hello", ContentStatus.Published);
        await StoreSiteAsync(tenant, "Private");
        return (tenant, type);
    }

    [Fact]
    public async Task An_anonymous_call_to_a_private_tenants_delivery_is_refused_and_a_live_tenant_serves_it()
    {
        var (tenant, type) = await PrivateTenantWithEntryAsync();
        var live = await _host.TenantAsync();
        var liveType = await _host.TypeAsync(live);
        await _host.EntryAsync(live, liveType, "hello", ContentStatus.Published);
        await StoreSiteAsync(live, "Live");

        var refused = await AnonymousIn(tenant).GetAsync($"/api/public/{type}/hello", Ct);
        var list = await AnonymousIn(tenant).GetAsync($"/api/public/{type}", Ct);
        var served = await AnonymousIn(live).GetAsync($"/api/public/{liveType}/hello", Ct);

        refused.StatusCode.Should().Be(HttpStatusCode.NotFound);
        refused.Headers.CacheControl!.NoStore.Should().BeTrue();
        list.StatusCode.Should().Be(HttpStatusCode.NotFound);
        served.StatusCode.Should().Be(HttpStatusCode.OK, "another tenant is unaffected");
    }

    [Fact]
    public async Task A_tenant_with_no_site_entry_or_no_mode_is_served()
    {
        var bare = await _host.TenantAsync();
        var bareType = await _host.TypeAsync(bare);
        await _host.EntryAsync(bare, bareType, "hello", ContentStatus.Published);
        var unset = await _host.TenantAsync();
        var unsetType = await _host.TypeAsync(unset);
        await _host.EntryAsync(unset, unsetType, "hello", ContentStatus.Published);
        await StoreSiteAsync(unset, null);
        var holding = await _host.TenantAsync();
        var holdingType = await _host.TypeAsync(holding);
        await _host.EntryAsync(holding, holdingType, "hello", ContentStatus.Published);
        await StoreSiteAsync(holding, "Holding");

        (await AnonymousIn(bare).GetAsync($"/api/public/{bareType}/hello", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await AnonymousIn(unset).GetAsync($"/api/public/{unsetType}/hello", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await AnonymousIn(holding).GetAsync($"/api/public/{holdingType}/hello", Ct)).StatusCode
            .Should().Be(HttpStatusCode.OK, "Holding is presentation only");
    }

    [Fact]
    public async Task A_link_to_the_whole_site_is_served_and_a_link_to_one_entry_a_revoked_one_and_a_wrong_key_are_not()
    {
        var (tenant, type) = await PrivateTenantWithEntryAsync();
        var entryId = await _host.EntryAsync(tenant, type, "other", ContentStatus.Published);
        var siteKey = await _host.StoreLinkAsync(tenant, _ => { });
        var entryKey = await _host.StoreLinkAsync(tenant, l => l.EntryId = entryId);
        var revoked = await _host.StoreLinkAsync(tenant, l => l.RevokedAt = DateTimeOffset.UtcNow.AddMinutes(-1));
        var expired = await _host.StoreLinkAsync(tenant, l => l.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1));
        var path = $"/api/public/{type}/hello";

        var served = await AnonymousIn(tenant, siteKey).GetAsync(path, Ct);
        served.StatusCode.Should().Be(HttpStatusCode.OK, await served.Content.ReadAsStringAsync(Ct));
        served.Headers.CacheControl!.NoStore.Should().BeTrue();
        served.Headers.CacheControl.Private.Should().BeTrue("a shared cache must not keep a private site's page");

        foreach (var key in new[] { entryKey, revoked, expired, "not-a-key" })
        {
            (await AnonymousIn(tenant, key).GetAsync(path, Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound, key);
        }

        var otherTenant = await _host.TenantAsync();
        await StoreSiteAsync(otherTenant, "Private");
        (await AnonymousIn(otherTenant, siteKey).GetAsync(path, Ct)).StatusCode
            .Should().Be(HttpStatusCode.NotFound, "a link opens its own tenant and no other");
    }

    [Fact]
    public async Task A_viewer_who_may_read_the_site_type_is_served_and_one_who_may_not_is_refused()
    {
        var (tenant, type) = await PrivateTenantWithEntryAsync();
        var viewer = await _host.EditorInAsync(tenant, "site", mayRead: true, mayUpdate: false);
        var outsider = await _host.EditorInAsync(tenant, "site", mayRead: false, mayUpdate: false);
        var admin = await _host.SuperAdminInAsync(tenant);
        var path = $"/api/public/{type}/hello";

        (await viewer.GetAsync(path, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await admin.GetAsync(path, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await outsider.GetAsync(path, Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_share_link_routes_stay_reachable_while_private()
    {
        var (tenant, _) = await PrivateTenantWithEntryAsync();
        var key = await _host.StoreLinkAsync(tenant, _ => { });

        var redeem = await AnonymousIn(tenant).PostAsJsonAsync("/api/public/site/share-links/redeem", new { key }, Ct);

        redeem.StatusCode.Should().Be(HttpStatusCode.OK, await redeem.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Every_public_route_of_a_private_tenant_is_refused_to_an_anonymous_caller()
    {
        var (tenant, _) = await PrivateTenantWithEntryAsync();
        var routes = _fixture.Services.GetServices<EndpointDataSource>()
            .SelectMany(s => s.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText is { } t
                        && t.StartsWith("/api/public", StringComparison.OrdinalIgnoreCase))
            .SelectMany(e => e.Metadata.OfType<IHttpMethodMetadata>().FirstOrDefault()?.HttpMethods
                .Select(m => (Method: m, Pattern: e.RoutePattern.RawText!)) ?? [])
            .Distinct()
            .ToList();

        routes.Should().Contain(r => r.Pattern.EndsWith("/sitemap.xml"));
        routes.Should().Contain(r => r.Pattern.EndsWith("/feed.xml"));
        routes.Should().Contain(r => r.Pattern.StartsWith("/api/public/files/"), "a module route under /api/public/");
        routes.Should().Contain(r => r.Pattern.StartsWith("/api/public/forms/"), "a module route under /api/public/");
        routes.Should().Contain(r => r.Pattern.StartsWith("/api/public/pages/"), "a module route under /api/public/");
        routes.Count.Should().BeGreaterThan(8);

        var entrances = new[] { "/api/public/site/share-links/redeem", "/api/public/site/share-links/open" };
        routes.Where(r => entrances.Contains(r.Pattern)).Select(r => r.Method).Should().OnlyContain(m => m == "POST");

        var client = AnonymousIn(tenant);
        var walked = routes.Where(r => r.Method != "OPTIONS" && r.Method != "HEAD"
                                       && !(r.Method == "POST" && entrances.Contains(r.Pattern))).ToList();
        walked.Should().NotBeEmpty();
        foreach (var (method, pattern) in walked)
        {
            var url = System.Text.RegularExpressions.Regex.Replace(pattern, @"\{[^}]+\}", "x");
            var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), url)
            {
                Content = method == "GET" ? null : JsonContent.Create(new { }),
            }, Ct);

            response.StatusCode.Should().Be(HttpStatusCode.NotFound, $"{method} {pattern}");
            response.Headers.CacheControl!.NoStore.Should().BeTrue($"{method} {pattern}");
        }
    }

    [Fact]
    public async Task A_get_on_the_share_link_path_is_a_delivery_read_and_is_refused()
    {
        var (tenant, _) = await PrivateTenantWithEntryAsync();

        var response = await AnonymousIn(tenant).GetAsync("/api/public/site/share-links", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Headers.CacheControl!.NoStore.Should().BeTrue("the refusal comes from the gate, not from a missing slug");
    }

    [Fact]
    public async Task The_refusal_does_not_depend_on_the_case_of_the_path_the_tenant_header_or_the_mode()
    {
        var (tenant, type) = await PrivateTenantWithEntryAsync();
        var lowerMode = await _host.TenantAsync();
        var lowerType = await _host.TypeAsync(lowerMode);
        await _host.EntryAsync(lowerMode, lowerType, "hello", ContentStatus.Published);
        await StoreSiteAsync(lowerMode, "private");

        var shouted = await AnonymousIn(tenant.ToUpperInvariant()).GetAsync($"/api/public/{type}/hello", Ct);
        var upperPath = await AnonymousIn(tenant).GetAsync($"/API/PUBLIC/{type}/hello", Ct);
        var lower = await AnonymousIn(lowerMode).GetAsync($"/api/public/{lowerType}/hello", Ct);

        shouted.StatusCode.Should().Be(HttpStatusCode.NotFound, "the tenant header is matched without regard to case");
        upperPath.StatusCode.Should().Be(HttpStatusCode.NotFound, "routing matches the path without regard to case");
        lower.StatusCode.Should().Be(HttpStatusCode.NotFound, "'private' is Private");
    }

    [Fact]
    public async Task A_second_published_site_entry_that_says_Live_does_not_open_a_private_tenant_and_a_draft_one_does_not_close_a_live_one()
    {
        var (tenant, type) = await PrivateTenantWithEntryAsync();
        await StoreSiteAsync(tenant, "Live");
        var draftOnly = await _host.TenantAsync();
        var draftType = await _host.TypeAsync(draftOnly);
        await _host.EntryAsync(draftOnly, draftType, "hello", ContentStatus.Published);
        await StoreSiteAsync(draftOnly, "Private", ContentStatus.Draft);

        (await AnonymousIn(tenant).GetAsync($"/api/public/{type}/hello", Ct)).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
        (await AnonymousIn(draftOnly).GetAsync($"/api/public/{draftType}/hello", Ct)).StatusCode
            .Should().Be(HttpStatusCode.OK, "an unpublished site entry is not what delivery reads");
    }

    [Fact]
    public async Task A_page_cached_while_the_tenant_was_live_is_not_served_once_it_is_private()
    {
        var tenant = await _host.TenantAsync();
        var type = await _host.TypeAsync(tenant);
        await _host.EntryAsync(tenant, type, "hello", ContentStatus.Published);
        var path = $"/api/public/{type}/hello";

        (await AnonymousIn(tenant).GetAsync(path, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await AnonymousIn(tenant).GetAsync(path, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        await StoreSiteAsync(tenant, "Private");

        (await AnonymousIn(tenant).GetAsync(path, Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_tenant_profile_lookup_hides_a_private_tenants_site_entry_from_an_anonymous_caller()
    {
        var tenant = await _host.TenantAsync();
        await StoreSiteAsync(tenant, "Private");
        var live = await _host.TenantAsync();
        await StoreSiteAsync(live, "Live");
        var siteKey = await _host.StoreLinkAsync(tenant, _ => { });

        var refused = await AnonymousIn("default").GetAsync($"/api/tenants/{tenant}/public", Ct);
        var withLink = await AnonymousIn("default", siteKey).GetAsync($"/api/tenants/{tenant}/public", Ct);
        var served = await AnonymousIn("default").GetAsync($"/api/tenants/{live}/public", Ct);

        refused.StatusCode.Should().Be(HttpStatusCode.NotFound);
        refused.Headers.CacheControl!.NoStore.Should().BeTrue();
        withLink.StatusCode.Should().Be(HttpStatusCode.OK, await withLink.Content.ReadAsStringAsync(Ct));
        served.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_site_entry_holding_Mode_under_two_spellings_is_private_when_either_says_Private()
    {
        var tenant = await _host.TenantAsync();
        var type = await _host.TypeAsync(tenant);
        await _host.EntryAsync(tenant, type, "hello", ContentStatus.Published);
        await StoreRawSiteAsync(tenant, new Dictionary<string, object>
        {
            ["mode"] = "Live",
            ["Mode"] = "Private",
        });

        (await AnonymousIn(tenant).GetAsync($"/api/public/{type}/hello", Ct)).StatusCode
            .Should().Be(HttpStatusCode.NotFound, "the first key spelled Mode says Live, the second says Private");
    }

    [Fact]
    public async Task An_event_stream_opened_while_live_ends_once_the_tenant_turns_private()
    {
        var tenant = await _host.TenantAsync();
        await StoreSiteAsync(tenant, "Live");
        using var resolver = FastEndpointsResolver.Keep();
        using var host = _fixture.WithSettings(new Dictionary<string, string?>
        {
            ["Delivery:Events:Enabled"] = "true",
            ["Delivery:Events:KeepAliveSeconds"] = "1",
            ["Delivery:Events:GateRecheckSeconds"] = "1",
        });
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant", tenant);

        using var response = await client.GetAsync("/api/public/events", HttpCompletionOption.ResponseHeadersRead, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(Ct));
        (await reader.ReadLineAsync(Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(20), Ct))
            .Should().NotBeNull("the stream is open and sends keepalives while the site is live");

        await StoreSiteAsync(tenant, "Private");

        var ended = false;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (DateTimeOffset.UtcNow < deadline && !ended)
        {
            ended = await reader.ReadLineAsync(Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(20), Ct) is null;
        }

        ended.Should().BeTrue("the stream completes when its caller no longer qualifies");
    }

    [Fact]
    public async Task A_head_request_to_a_share_link_path_is_refused_and_a_trailing_slash_does_not_hide_the_exemption()
    {
        var (tenant, _) = await PrivateTenantWithEntryAsync();
        var key = await _host.StoreLinkAsync(tenant, _ => { });
        var client = AnonymousIn(tenant);

        foreach (var path in new[] { "/api/public/site/share-links/redeem", "/api/public/site/share-links/open" })
        {
            var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, path), Ct);
            head.StatusCode.Should().Be(HttpStatusCode.NotFound, path);
            head.Headers.CacheControl!.NoStore.Should().BeTrue($"HEAD {path} is a delivery read and the gate answered it");
        }

        var redeem = await client.PostAsJsonAsync("/api/public/site/share-links/redeem/", new { key }, Ct);
        redeem.StatusCode.Should().Be(HttpStatusCode.OK, await redeem.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task A_response_served_to_a_link_holder_while_private_is_not_marked_no_store_once_the_tenant_is_live_again()
    {
        var (tenant, type) = await PrivateTenantWithEntryAsync();
        var key = await _host.StoreLinkAsync(tenant, _ => { });
        var path = $"/api/public/{type}/hello";

        var served = await AnonymousIn(tenant, key).GetAsync(path, Ct);
        served.StatusCode.Should().Be(HttpStatusCode.OK);
        served.Headers.CacheControl!.NoStore.Should().BeTrue();

        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        await using (var session = store.LightweightSession(tenant))
        {
            var sites = await session.Query<Content>().Where(c => c.ContentType == "site").ToListAsync(Ct);
            sites.Should().NotBeEmpty();
            foreach (var site in sites)
            {
                site.Data["Mode"] = "Live";
                session.Store(site);
            }

            await session.SaveChangesAsync(Ct);
        }

        var live = await AnonymousIn(tenant).GetAsync(path, Ct);
        live.StatusCode.Should().Be(HttpStatusCode.OK);
        live.Headers.CacheControl?.NoStore.Should().NotBe(true, "the private header must not ride in a cached entry");
        live.Headers.CacheControl?.Private.Should().NotBe(true);
    }

    private static readonly (string Match, bool Prefix, string Reason)[] ReviewedAnonymousRoutes =
    [
        ("/api/auth/", true, "sign in, register, MFA and social sign in: identity, no tenant content"),
        ("/api/tenants/by-host/{host}", false, "returns a tenant handle only"),
        ("/api/tenants/tls-ask", false, "a proxy asks whether a host is a tenant domain; registry only"),
        ("/api/tenants/{handle}/public", false, "reads a site entry, so it applies the Private rule itself"),
        ("/ofrep/", true, "feature flag evaluation: flags, no content"),
        ("/api/feature-flags", false, "feature flag evaluation: flags, no content"),
        ("/api/client-errors", false, "a write of client error reports"),
        ("/api/pwa/report", false, "a write of install and usage reports"),
        ("/api/payments/webhooks/{provider}", false, "a signed provider webhook; answers a bare status code and returns no body"),
        ("/api/renderer/blocks/schema", false, "a write guarded by the renderer key"),
        ("/api/_test/", true, "test host only"),
    ];

    [Fact]
    public void Every_anonymous_route_outside_api_public_is_reviewed_as_serving_no_tenant_content()
    {
        var anonymous = _fixture.Services.GetServices<EndpointDataSource>()
            .SelectMany(s => s.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            .Select(e => e.RoutePattern.RawText!)
            .Where(t => !t.StartsWith("/api/public", StringComparison.OrdinalIgnoreCase))
            .Distinct()
            .ToList();

        anonymous.Should().NotBeEmpty();
        var unreviewed = anonymous.Where(t => !ReviewedAnonymousRoutes.Any(r => Matches(r, t))).ToList();
        unreviewed.Should().BeEmpty(
            "an anonymous route outside /api/public is not behind the Private gate: review it, then list it with a reason");

        foreach (var entry in ReviewedAnonymousRoutes)
        {
            anonymous.Should().Contain(t => Matches(entry, t), entry.Reason);
        }
    }

    private static bool Matches((string Match, bool Prefix, string Reason) entry, string pattern) =>
        entry.Prefix
            ? pattern.StartsWith(entry.Match, StringComparison.OrdinalIgnoreCase)
            : string.Equals(pattern, entry.Match, StringComparison.OrdinalIgnoreCase);
}
