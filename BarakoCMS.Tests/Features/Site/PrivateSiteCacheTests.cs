using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.Site;

/// <summary>
/// The private site check is cached per tenant (#1175): a public tenant costs no read for it, and
/// every write to a site entry is seen by the next request.
/// </summary>
[Collection("Sequential")]
public class PrivateSiteCacheTests
{
    private const string GateStatement = "lower(e.key) = 'mode'";

    private readonly IntegrationTestFixture _fixture;
    private readonly ShareLinkTestHost _host;

    public PrivateSiteCacheTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        _host = new ShareLinkTestHost(fixture);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<Guid> StoreSiteAsync(string tenant, string mode, ContentStatus status = ContentStatus.Published)
    {
        var id = Guid.NewGuid();
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession(tenant);
        session.Store(new Content
        {
            Id = id,
            ContentType = "site",
            Status = status,
            Sensitivity = SensitivityLevel.Public,
            Data = new Dictionary<string, object> { ["Name"] = "Club", ["Mode"] = mode },
        });
        await session.SaveChangesAsync(Ct);
        return id;
    }

    private async Task ChangeSiteAsync(string tenant, Guid id, Action<Content> change)
    {
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession(tenant);
        var site = await session.LoadAsync<Content>(id, Ct);
        change(site!);
        session.Store(site!);
        await session.SaveChangesAsync(Ct);
    }

    private HttpClient AnonymousIn(string tenant, string? traceparent = null)
    {
        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant", tenant);
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, ShareLinkTestHost.NextIp());
        if (traceparent is not null)
        {
            client.DefaultRequestHeaders.Add("traceparent", traceparent);
        }

        return client;
    }

    private async Task<HttpStatusCode> StatusAsync(string tenant, string type) =>
        (await AnonymousIn(tenant).GetAsync($"/api/public/{type}/hello", Ct)).StatusCode;

    private async Task<(string Tenant, string Type)> PublicTenantAsync()
    {
        var tenant = await _host.TenantAsync();
        var type = await _host.TypeAsync(tenant);
        await _host.EntryAsync(tenant, type, "hello", ContentStatus.Published);
        return (tenant, type);
    }

    [Fact]
    public async Task A_tenant_switched_to_private_refuses_the_very_next_anonymous_request()
    {
        var (tenant, type) = await PublicTenantAsync();
        var site = await StoreSiteAsync(tenant, "Live");
        (await StatusAsync(tenant, type)).Should().Be(HttpStatusCode.OK, "this request caches the public answer");

        await ChangeSiteAsync(tenant, site, s => s.Data["Mode"] = "Private");

        (await StatusAsync(tenant, type)).Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_site_entry_published_after_the_public_answer_was_cached_is_seen_at_once()
    {
        var (tenant, type) = await PublicTenantAsync();
        var site = await StoreSiteAsync(tenant, "Private", ContentStatus.Draft);
        (await StatusAsync(tenant, type)).Should().Be(HttpStatusCode.OK, "a draft site entry does not make a tenant private");

        await ChangeSiteAsync(tenant, site, s => s.Status = ContentStatus.Published);

        (await StatusAsync(tenant, type)).Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_new_private_site_entry_is_seen_at_once_by_a_tenant_with_none()
    {
        var (tenant, type) = await PublicTenantAsync();
        (await StatusAsync(tenant, type)).Should().Be(HttpStatusCode.OK, "this request caches the public answer");

        await StoreSiteAsync(tenant, "Private");

        (await StatusAsync(tenant, type)).Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_private_site_entry_that_is_unpublished_or_deleted_opens_the_tenant_at_once()
    {
        var (tenant, type) = await PublicTenantAsync();
        var unpublished = await StoreSiteAsync(tenant, "Private");
        (await StatusAsync(tenant, type)).Should().Be(HttpStatusCode.NotFound);

        await ChangeSiteAsync(tenant, unpublished, s => s.Status = ContentStatus.Draft);
        (await StatusAsync(tenant, type)).Should().Be(HttpStatusCode.OK);

        var deleted = await StoreSiteAsync(tenant, "Private");
        (await StatusAsync(tenant, type)).Should().Be(HttpStatusCode.NotFound);

        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        await using (var session = store.LightweightSession(tenant))
        {
            session.Delete<Content>(deleted);
            await session.SaveChangesAsync(Ct);
        }

        (await StatusAsync(tenant, type)).Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_bulk_delete_of_content_clears_the_cached_answer()
    {
        var (tenant, type) = await PublicTenantAsync();
        await StoreSiteAsync(tenant, "Private", ContentStatus.Draft);
        (await StatusAsync(tenant, type)).Should().Be(HttpStatusCode.OK, "this request caches the public answer");

        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        await using (var session = store.LightweightSession(tenant))
        {
            session.DeleteWhere<Content>(c => c.ContentType == "site");
            session.Store(new Content
            {
                Id = Guid.NewGuid(),
                ContentType = "site",
                Status = ContentStatus.Published,
                Sensitivity = SensitivityLevel.Public,
                Data = new Dictionary<string, object> { ["Mode"] = "Private" },
            });
            await session.SaveChangesAsync(Ct);
        }

        (await StatusAsync(tenant, type)).Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_second_anonymous_request_to_a_public_tenant_runs_no_gate_read()
    {
        var (tenant, type) = await PublicTenantAsync();
        await StoreSiteAsync(tenant, "Live");

        var first = await GateReadsAsync(tenant, type);
        var second = await GateReadsAsync(tenant, type);

        first.Should().Be(1, "the first request has no cached answer, or the counter sees nothing");
        second.Should().Be(0);
    }

    private async Task<int> GateReadsAsync(string tenant, string type)
    {
        var traceId = ActivityTraceId.CreateRandom();
        var statements = new ConcurrentQueue<string>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name is "Npgsql" or "Microsoft.AspNetCore",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.Source.Name != "Npgsql" || activity.TraceId != traceId)
                {
                    return;
                }

                statements.Enqueue(activity.GetTagItem("db.statement") as string
                                   ?? activity.GetTagItem("db.query.text") as string
                                   ?? string.Empty);
            },
        };
        ActivitySource.AddActivityListener(listener);

        var client = AnonymousIn(tenant, $"00-{traceId}-{ActivitySpanId.CreateRandom()}-01");
        var response = await client.GetAsync($"/api/public/{type}/hello", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        statements.Should().NotBeEmpty("the request's own database spans were heard, or the count proves nothing");
        return statements.Count(s => s.Contains(GateStatement));
    }
}
