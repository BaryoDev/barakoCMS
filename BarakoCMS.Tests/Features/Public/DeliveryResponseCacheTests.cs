using System.Net;
using System.Text.Json;
using barakoCMS.Infrastructure.Viewers;
using barakoCMS.Models;
using BarakoCMS.Tests.Features.Site;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Features.Public;

/// <summary>
/// The delivery response cache (#562): a second identical read does not reach Postgres, no entry
/// crosses a tenant or an audience, a publish and an unpublish show on the next read, and the cache
/// is off and bounded unless it is told otherwise.
/// </summary>
/// <remarks>
/// Each host keeps its own cache, so every write goes through the host that is read from. The count
/// is Marten's own statements, not a stopwatch.
/// </remarks>
[Collection("Sequential")]
public class DeliveryResponseCacheTests(IntegrationTestFixture fixture)
{
    private static CancellationToken Ct => ShareLinkTestHost.Ct;

    private sealed record Host(WebApplicationFactory<Program> Factory, CapturingMartenLogger Logger)
    {
        public IDocumentStore Store => Factory.Services.GetRequiredService<IDocumentStore>();

        public IConfiguration Config => Factory.Services.GetRequiredService<IConfiguration>();
    }

    private Host HostWith(params (string Key, string Value)[] settings)
    {
        var logger = new CapturingMartenLogger();
        var all = settings.ToDictionary(s => s.Key, s => (string?)s.Value);
        var derived = fixture.WithSettings(all).WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.ConfigureMarten(options => options.Logger(logger))));
        return new Host(derived, logger);
    }

    private Host Cached(params (string Key, string Value)[] more) =>
        HostWith([("Delivery:ResponseCache:Enabled", "true"), .. more]);

    private static HttpClient In(Host host, string? tenant)
    {
        var client = host.Factory.CreateClient();
        if (tenant is not null)
            client.DefaultRequestHeaders.Add("X-Tenant", tenant);
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, ShareLinkTestHost.NextIp());
        return client;
    }

    private async Task<string> TenantAsync()
    {
        var slug = $"rsp-{Guid.NewGuid():N}"[..14].ToLowerInvariant();
        using var scope = fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = $"0 {slug}" });
        await session.SaveChangesAsync(Ct);
        return slug;
    }

    private static async Task DefineAsync(Host host, string tenant, string type)
    {
        await using var session = host.Store.LightweightSession(tenant);
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = type,
            DisplayName = type,
            IsPubliclyDeliverable = true,
            Fields =
            [
                new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string" },
                new FieldDefinition { Name = "Slug", DisplayName = "Slug", Type = "slug" },
                new FieldDefinition { Name = "Body", DisplayName = "Body", Type = "markdown" },
                new FieldDefinition { Name = "Perk", DisplayName = "Perk", Type = "string", Sensitivity = SensitivityLevel.Members },
            ],
        });
        await session.SaveChangesAsync(Ct);
    }

    private static async Task<Guid> EntryAsync(
        Host host, string tenant, string type, string slug, string marker,
        ContentStatus status = ContentStatus.Published,
        SensitivityLevel sensitivity = SensitivityLevel.Public,
        Guid? id = null)
    {
        var entry = id ?? Guid.NewGuid();
        await using var session = host.Store.LightweightSession(tenant);
        session.Store(new Content
        {
            Id = entry,
            ContentType = type,
            Status = status,
            Sensitivity = sensitivity,
            Data = new Dictionary<string, object> { ["Title"] = marker, ["Slug"] = slug, ["Body"] = marker },
            SearchText = marker,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await session.SaveChangesAsync(Ct);
        return entry;
    }

    private static async Task<string> BodyAsync(HttpClient client, string url, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await client.GetAsync(url, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.Should().Be(expected, body);
        return body;
    }

    private static int Reads(Host host, string type) =>
        host.Logger.Commands.Count(c => c.Parameters.Contains(type));

    [Fact]
    public async Task A_second_identical_read_does_not_reach_the_database()
    {
        var host = Cached();
        var tenant = await TenantAsync();
        var type = $"rsp{Guid.NewGuid():n}"[..12];
        var marker = $"MARK{Guid.NewGuid():N}";
        await DefineAsync(host, tenant, type);
        await EntryAsync(host, tenant, type, "the-entry", marker);
        using var client = In(host, tenant);

        var first = await BodyAsync(client, $"/api/public/{type}/the-entry");
        first.Should().Contain(marker);
        Reads(host, type).Should().BePositive("the first read has to ask Postgres, or the check below proves nothing");

        host.Logger.Commands.Clear();
        var second = await BodyAsync(client, $"/api/public/{type}/the-entry");

        second.Should().Be(first);
        Reads(host, type).Should().Be(0, "a repeat is answered from the cache, not from Postgres");

        var list = await BodyAsync(client, $"/api/public/{type}");
        host.Logger.Commands.Clear();
        (await BodyAsync(client, $"/api/public/{type}")).Should().Be(list);
        Reads(host, type).Should().Be(0);
    }

    [Fact]
    public async Task A_read_is_not_cached_unless_the_cache_is_turned_on()
    {
        var host = HostWith();
        var tenant = await TenantAsync();
        var type = $"rsp{Guid.NewGuid():n}"[..12];
        await DefineAsync(host, tenant, type);
        await EntryAsync(host, tenant, type, "the-entry", "plain");
        using var client = In(host, tenant);

        await BodyAsync(client, $"/api/public/{type}/the-entry");
        host.Logger.Commands.Clear();
        await BodyAsync(client, $"/api/public/{type}/the-entry");

        Reads(host, type).Should().BePositive("with the cache off every read asks Postgres, as before");
    }

    [Fact]
    public async Task A_tenant_never_gets_another_tenants_bytes_for_the_same_path()
    {
        var host = Cached();
        var a = await TenantAsync();
        var b = await TenantAsync();
        var type = $"rsp{Guid.NewGuid():n}"[..12];
        var markerA = $"AAAA{Guid.NewGuid():N}";
        var markerB = $"BBBB{Guid.NewGuid():N}";
        await DefineAsync(host, a, type);
        await DefineAsync(host, b, type);
        await EntryAsync(host, a, type, "same-slug", markerA);
        await EntryAsync(host, b, type, "same-slug", markerB);
        using var clientA = In(host, a);
        using var clientB = In(host, b);

        var url = $"/api/public/{type}/same-slug";
        (await BodyAsync(clientA, url)).Should().Contain(markerA);
        (await BodyAsync(clientA, url)).Should().Contain(markerA);

        var forB = await BodyAsync(clientB, url);
        forB.Should().Contain(markerB);
        forB.Should().NotContain(markerA);

        var list = await BodyAsync(clientB, $"/api/public/{type}");
        list.Should().Contain(markerB);
        list.Should().NotContain(markerA);
    }

    [Fact]
    public async Task A_viewer_and_an_anonymous_caller_never_share_an_entry()
    {
        var host = Cached();
        var tenant = await TenantAsync();
        var type = $"rsp{Guid.NewGuid():n}"[..12];
        var open = $"OPEN{Guid.NewGuid():N}";
        var members = $"MEMB{Guid.NewGuid():N}";
        await DefineAsync(host, tenant, type);
        await EntryAsync(host, tenant, type, "open-entry", open);
        await EntryAsync(host, tenant, type, "members-entry", members, sensitivity: SensitivityLevel.Members);
        var viewer = ViewerToken.Create(host.Config, tenant, "member-1").Token;
        var url = $"/api/public/{type}";

        using var anonymous = In(host, tenant);
        using var member = In(host, tenant);
        member.DefaultRequestHeaders.Add(ViewerToken.Header, viewer);

        var anonymousFirst = await BodyAsync(anonymous, url);
        anonymousFirst.Should().Contain(open);
        anonymousFirst.Should().NotContain(members);
        await BodyAsync(anonymous, url);

        var forViewer = await BodyAsync(member, url);
        forViewer.Should().Contain(members, "a cached anonymous list must not stand in for the viewer's");

        var anonymousAgain = await BodyAsync(anonymous, url);
        anonymousAgain.Should().NotContain(members, "what a viewer was shown must not be stored for anonymous callers");
    }

    [Fact]
    public async Task A_publish_shows_on_the_next_read_and_an_unpublish_takes_the_entry_away()
    {
        var host = Cached();
        var tenant = await TenantAsync();
        var type = $"rsp{Guid.NewGuid():n}"[..12];
        var existing = $"OLD{Guid.NewGuid():N}";
        var fresh = $"NEW{Guid.NewGuid():N}";
        await DefineAsync(host, tenant, type);
        await EntryAsync(host, tenant, type, "existing", existing);
        var id = await EntryAsync(host, tenant, type, "fresh", fresh, ContentStatus.Draft);
        using var client = In(host, tenant);
        var list = $"/api/public/{type}";

        var before = await BodyAsync(client, list);
        before.Should().Contain(existing);
        before.Should().NotContain(fresh);
        await BodyAsync(client, list);
        await BodyAsync(client, $"/api/public/{type}/fresh", HttpStatusCode.NotFound);

        await EntryAsync(host, tenant, type, "fresh", fresh, ContentStatus.Published, id: id);

        (await BodyAsync(client, list)).Should().Contain(fresh, "the publish evicts the cached list within the same request cycle");
        (await BodyAsync(client, $"/api/public/{type}/fresh")).Should().Contain(fresh);
        (await BodyAsync(client, $"/api/public/{type}/fresh")).Should().Contain(fresh);

        await EntryAsync(host, tenant, type, "fresh", fresh, ContentStatus.Draft, id: id);

        (await BodyAsync(client, list)).Should().NotContain(fresh);
        await BodyAsync(client, $"/api/public/{type}/fresh", HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_publish_in_one_tenant_leaves_another_tenants_cache_alone()
    {
        var host = Cached();
        var a = await TenantAsync();
        var b = await TenantAsync();
        var type = $"rsp{Guid.NewGuid():n}"[..12];
        var marker = $"KEEP{Guid.NewGuid():N}";
        await DefineAsync(host, a, type);
        await DefineAsync(host, b, type);
        await EntryAsync(host, b, type, "kept", marker);
        using var clientB = In(host, b);

        await BodyAsync(clientB, $"/api/public/{type}/kept");
        await EntryAsync(host, a, type, "other", "elsewhere");
        host.Logger.Commands.Clear();
        await BodyAsync(clientB, $"/api/public/{type}/kept");

        Reads(host, type).Should().Be(0, "a write in tenant A evicts only tenant A's tags");
    }

    [Fact]
    public async Task A_request_carrying_credentials_is_neither_served_from_nor_stored_in_the_cache()
    {
        var host = Cached();
        var tenant = await TenantAsync();
        var type = $"rsp{Guid.NewGuid():n}"[..12];
        await DefineAsync(host, tenant, type);
        await EntryAsync(host, tenant, type, "the-entry", "marker");
        var viewer = ViewerToken.Create(host.Config, tenant, "member-1").Token;
        using var member = In(host, tenant);
        member.DefaultRequestHeaders.Add(ViewerToken.Header, viewer);

        await BodyAsync(member, $"/api/public/{type}/the-entry");
        host.Logger.Commands.Clear();
        await BodyAsync(member, $"/api/public/{type}/the-entry");

        Reads(host, type).Should().BePositive("a viewer's read is built for the viewer every time");
    }

    [Fact]
    public async Task A_body_over_the_size_bound_is_served_but_not_kept()
    {
        var host = Cached(("Delivery:ResponseCache:MaxBodyKilobytes", "1"));
        var tenant = await TenantAsync();
        var type = $"rsp{Guid.NewGuid():n}"[..12];
        await DefineAsync(host, tenant, type);
        var big = new string('x', 8 * 1024);
        await EntryAsync(host, tenant, type, "big-entry", big);
        using var client = In(host, tenant);

        var first = await BodyAsync(client, $"/api/public/{type}/big-entry");
        first.Length.Should().BeGreaterThan(1024);
        host.Logger.Commands.Clear();
        await BodyAsync(client, $"/api/public/{type}/big-entry");

        Reads(host, type).Should().BePositive("a response past MaxBodyKilobytes is never stored");
    }

    [Fact]
    public async Task The_tags_the_cache_evicts_by_are_the_tags_a_read_sends()
    {
        var host = Cached();
        var tenant = await TenantAsync();
        var type = $"rsp{Guid.NewGuid():n}"[..12];
        await DefineAsync(host, tenant, type);
        var id = await EntryAsync(host, tenant, type, "tagged", "tag-marker");
        using var client = In(host, tenant);

        var response = await client.GetAsync($"/api/public/{type}/tagged", Ct);
        var tags = response.Headers.GetValues("Surrogate-Key").SelectMany(v => v.Split(' ')).ToList();

        tags.Should().NotBeEmpty();
        tags.Should().Contain(barakoCMS.Infrastructure.Caching.DeliveryCache.Tag(tenant, barakoCMS.Infrastructure.Caching.CacheScope.Entry(id)));
        tags.Should().Contain(barakoCMS.Infrastructure.Caching.DeliveryCache.Tag(tenant, barakoCMS.Infrastructure.Caching.CacheScope.Type(type)));
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).Should().NotBeNull();
    }
}
