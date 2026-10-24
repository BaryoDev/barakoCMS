using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using barakoCMS.Events;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;

namespace BarakoCMS.Tests.Features.Tenants;

/// <summary>
/// A tenant's Status is read once, at resolution: a tenant that is not Active answers 404 to
/// delivery and to anyone who is not signed in to it, 403 to its own sessions on authoring routes,
/// and its schedules wait until it is Active again (#894).
/// </summary>
[Collection("Integration4")]
public class TenantStatusTests
{
    private readonly IntegrationTestFixture _fixture;

    public TenantStatusTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<HttpClient> PlatformAdminAsync()
    {
        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await _fixture.StoredUserTokenAsync("SuperAdmin"));
        return client;
    }

    private async Task<HttpResponseMessage> SetStatusAsync(string slug, object body)
    {
        var admin = await PlatformAdminAsync();
        return await admin.PutAsJsonAsync($"/api/tenants/{slug}", body, Ct);
    }

    private async Task SetStatusOkAsync(string slug, string status)
    {
        var response = await SetStatusAsync(slug, new { name = slug, status });
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>A tenant with a published site entry, so anonymous delivery has something to serve.</summary>
    private async Task<string> TenantWithSiteAsync()
    {
        var slug = await TenantProfileSeed.TenantAsync(_fixture, Ct);
        await TenantProfileSeed.StoreSiteTypeAsync(_fixture, slug, Ct);
        var admin = await TenantProfileSeed.AdminInAsync(_fixture, slug, Ct);
        var id = await TenantProfileSeed.CreateSiteEntryAsync(admin, new Dictionary<string, object> { ["Name"] = "Open" }, Ct);
        await TenantProfileSeed.PublishAsync(admin, id, Ct);
        return slug;
    }

    private Task<HttpResponseMessage> DeliverByHeaderAsync(string slug) =>
        TenantProfileSeed.Anonymous(_fixture, slug).GetAsync("/api/public/site", Ct);

    private Task<HttpResponseMessage> DeliverBySubdomainAsync(string slug) =>
        TenantProfileSeed.Anonymous(_fixture).GetAsync($"http://{slug}.example.com/api/public/site", Ct);

    private static async Task ShouldServeTheSiteAsync(HttpResponseMessage response, string because)
    {
        response.StatusCode.Should().Be(HttpStatusCode.OK, because + ": " + await response.Content.ReadAsStringAsync(Ct));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("items").GetArrayLength().Should().Be(1, because);
    }

    [Theory]
    [InlineData("Suspended")]
    [InlineData("Archived")]
    public async Task Anonymous_delivery_for_a_tenant_that_is_not_active_answers_404_by_header_and_by_subdomain(string status)
    {
        var slug = await TenantWithSiteAsync();

        await ShouldServeTheSiteAsync(await DeliverByHeaderAsync(slug), "the tenant is active and its site is published");
        await ShouldServeTheSiteAsync(await DeliverBySubdomainAsync(slug), "the subdomain names the same active tenant");

        await SetStatusOkAsync(slug, status);

        (await DeliverByHeaderAsync(slug)).StatusCode.Should().Be(HttpStatusCode.NotFound, "the tenant is {0}", status);
        (await DeliverBySubdomainAsync(slug)).StatusCode.Should().Be(HttpStatusCode.NotFound, "the tenant is {0}", status);

        await SetStatusOkAsync(slug, "Active");

        await ShouldServeTheSiteAsync(await DeliverByHeaderAsync(slug), "the tenant is active again");
    }

    [Fact]
    public async Task A_custom_domain_of_a_suspended_tenant_does_not_fall_through_to_the_default_tenant()
    {
        var slug = await TenantWithSiteAsync();
        var domain = $"{slug}.example";
        var admin = await PlatformAdminAsync();
        (await admin.PutAsJsonAsync($"/api/tenants/{slug}", new { name = slug, domains = new[] { domain } }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var client = TenantProfileSeed.Anonymous(_fixture);
        await ShouldServeTheSiteAsync(await client.GetAsync($"http://{domain}/api/public/site", Ct), "the domain is the active tenant's");

        await SetStatusOkAsync(slug, "Suspended");

        var refused = await client.GetAsync($"http://{domain}/api/public/site", Ct);
        refused.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "the domain still names the suspended tenant, rather than dropping out of the map and serving the default tenant's site");
    }

    [Fact]
    public async Task A_member_signed_in_to_a_suspended_tenant_gets_403_from_authoring_and_404_from_delivery()
    {
        var slug = await TenantWithSiteAsync();
        var member = await TenantProfileSeed.AdminInAsync(_fixture, slug, Ct);

        var before = await member.GetAsync("/api/contents", Ct);
        before.StatusCode.Should().Be(HttpStatusCode.OK, "the control: the member's session works while the tenant is active");

        await SetStatusOkAsync(slug, "Suspended");

        var authoring = await member.GetAsync("/api/contents", Ct);
        authoring.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await authoring.Content.ReadAsStringAsync(Ct)).Should().Be("This tenant is suspended.");

        var write = await member.PostAsJsonAsync("/api/contents", new { contentType = "site", data = new { Name = "x" } }, Ct);
        write.StatusCode.Should().Be(HttpStatusCode.Forbidden, "a write is refused like a read");

        (await member.GetAsync("/api/public/site", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound,
            "delivery answers 404 whoever asks");
    }

    [Fact]
    public async Task A_session_for_another_tenant_learns_nothing_about_a_suspended_one()
    {
        var suspended = await TenantWithSiteAsync();
        var other = await TenantProfileSeed.TenantAsync(_fixture, Ct);
        var outsider = await TenantProfileSeed.AdminInAsync(_fixture, other, Ct);

        await SetStatusOkAsync(suspended, "Suspended");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/contents");
        request.Headers.Add("X-Tenant", suspended);
        outsider.DefaultRequestHeaders.Remove("X-Tenant");
        var response = await outsider.SendAsync(request, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "only the tenant's own sessions are told it is suspended");
        (await response.Content.ReadAsStringAsync(Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_token_with_no_tenant_claim_learns_nothing_about_a_suspended_tenant()
    {
        var suspended = await TenantWithSiteAsync();
        await SetStatusOkAsync(suspended, "Suspended");

        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _fixture.CreateToken(["Admin"]));
        client.DefaultRequestHeaders.Add("X-Tenant", suspended);
        var response = await client.GetAsync("/api/contents", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound, await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Status_is_written_and_returned_with_IsActive_derived_from_it()
    {
        var slug = await TenantProfileSeed.TenantAsync(_fixture, Ct);

        var archived = await SetStatusAsync(slug, new { name = slug, status = "Archived" });
        archived.StatusCode.Should().Be(HttpStatusCode.OK, await archived.Content.ReadAsStringAsync(Ct));
        var body = await archived.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("status").GetString().Should().Be("Archived");
        body.GetProperty("isActive").GetBoolean().Should().BeFalse();

        var echoed = await SetStatusAsync(slug, new { name = slug, isActive = false });
        (await echoed.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("status").GetString().Should().Be("Archived",
            "a client that echoes isActive false back does not move an archived tenant to suspended");

        var conflict = await SetStatusAsync(slug, new { name = slug, status = "Active", isActive = false });
        conflict.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var neither = await SetStatusAsync(slug, new { name = slug });
        (await neither.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("status").GetString().Should().Be("Active",
            "leaving both out activates the tenant, which is what leaving isActive out always did");
    }

    /// <summary>The raw document, as the migration file and an earlier release read it.</summary>
    private async Task<JsonElement> StoredJsonAsync(Guid id)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("select data::text from public.mt_doc_tenants where id = @id", connection);
        command.Parameters.AddWithValue("id", id);
        var json = (string)(await command.ExecuteScalarAsync(Ct))!;
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private async Task<Guid> StoreAsOldReleaseAsync(string slug, bool? isActive)
    {
        var id = Guid.NewGuid();
        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new Tenant { Id = id, Slug = slug, Name = slug });
            await session.SaveChangesAsync(Ct);
        }

        // What a 4.9 document looks like: IsActive, or nothing at all, and no Status.
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync(Ct);
        var patch = isActive is null
            ? "update public.mt_doc_tenants set data = data - 'Status' - 'IsActive' where id = @id"
            : "update public.mt_doc_tenants set data = (data - 'Status') || jsonb_build_object('IsActive', @active) where id = @id";
        await using var command = new NpgsqlCommand(patch, connection);
        command.Parameters.AddWithValue("id", id);
        if (isActive is not null)
        {
            command.Parameters.AddWithValue("active", isActive.Value);
        }
        await command.ExecuteNonQueryAsync(Ct);
        _fixture.Services.GetRequiredService<ITenantDomainSource>().Invalidate();
        return id;
    }

    [Fact]
    public async Task The_migration_writes_Status_from_IsActive_in_the_encoding_the_serializer_uses()
    {
        var off = await StoreAsOldReleaseAsync($"ts-{Guid.NewGuid():N}"[..16], isActive: false);
        var on = await StoreAsOldReleaseAsync($"ts-{Guid.NewGuid():N}"[..16], isActive: true);
        var bare = await StoreAsOldReleaseAsync($"ts-{Guid.NewGuid():N}"[..16], isActive: null);

        (await StoredJsonAsync(off)).TryGetProperty("Status", out _).Should().BeFalse("the control: the row is as 4.9 left it");

        var sql = await File.ReadAllTextAsync(Path.Combine(RepositoryRoot(), "migrations", "5.0.0", "tenant-status.sql"), Ct);
        await using (var connection = new NpgsqlConnection(_fixture.ConnectionString))
        {
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync(Ct);
        }

        (await StoredJsonAsync(off)).GetProperty("Status").GetInt32().Should().Be((int)TenantStatus.Suspended);
        (await StoredJsonAsync(on)).GetProperty("Status").GetInt32().Should().Be((int)TenantStatus.Active);
        (await StoredJsonAsync(bare)).GetProperty("Status").GetInt32().Should().Be((int)TenantStatus.Active);

        // What the file writes is what Marten writes for the same value, or a migrated row and one
        // saved by the API would disagree.
        var written = new Tenant { Id = Guid.NewGuid(), Slug = $"ts-{Guid.NewGuid():N}"[..16], Status = TenantStatus.Suspended };
        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(written);
            await session.SaveChangesAsync(Ct);
        }

        var stored = await StoredJsonAsync(written.Id);
        stored.GetProperty("Status").GetInt32().Should().Be((int)TenantStatus.Suspended);
        stored.GetProperty("IsActive").GetBoolean().Should().BeFalse("IsActive is still written, for a release that reads only it");
    }

    [Fact]
    public async Task A_tenant_stored_by_an_earlier_release_as_inactive_is_refused_before_the_migration_runs()
    {
        var slug = await TenantWithSiteAsync();
        await ShouldServeTheSiteAsync(await DeliverByHeaderAsync(slug), "the control: the tenant is active");

        await using (var connection = new NpgsqlConnection(_fixture.ConnectionString))
        {
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand(
                "update public.mt_doc_tenants set data = (data - 'Status') || '{\"IsActive\": false}'::jsonb where data ->> 'Slug' = @slug",
                connection);
            command.Parameters.AddWithValue("slug", slug);
            (await command.ExecuteNonQueryAsync(Ct)).Should().Be(1);
        }

        _fixture.Services.GetRequiredService<ITenantDomainSource>().Invalidate();

        (await DeliverByHeaderAsync(slug)).StatusCode.Should().Be(HttpStatusCode.NotFound,
            "IsActive false with no Status reads as Suspended");
    }

    private async Task<(string Slug, IDocumentStore Store)> TenantWithStoreAsync()
    {
        var slug = await TenantProfileSeed.TenantAsync(_fixture, Ct);
        return (slug, _fixture.Services.GetRequiredService<IDocumentStore>());
    }

    private static async Task<Guid> ScheduledAsync(IDocumentStore store, string slug, DateTime publishAt, DateTime? unpublishAt)
    {
        var id = Guid.NewGuid();
        await using var session = store.LightweightSession(slug);
        session.Store(new Content
        {
            Id = id,
            ContentType = "article",
            Status = ContentStatus.Scheduled,
            ScheduledPublishAt = publishAt,
            ScheduledUnpublishAt = unpublishAt,
        });
        await session.SaveChangesAsync(Ct);
        return id;
    }

    private static async Task<Content> LoadAsync(IDocumentStore store, string slug, Guid id)
    {
        await using var session = store.QuerySession(slug);
        var content = await session.LoadAsync<Content>(id, Ct);
        content.Should().NotBeNull();
        return content!;
    }

    private async Task SweepAsync(IDocumentStore store)
    {
        var logger = _fixture.Services.GetRequiredService<ILogger<ScheduledContentService>>();
        (await new ScheduledContentService(store, logger).TrySweepAllTenantsAsync(DateTime.UtcNow, Ct))
            .Should().BeTrue("nothing else holds the sweep lock");
    }

    [Fact]
    public async Task A_schedule_that_falls_due_while_its_tenant_is_suspended_waits_and_applies_on_reactivation()
    {
        var (slug, store) = await TenantWithStoreAsync();
        await SetStatusOkAsync(slug, "Suspended");
        var id = await ScheduledAsync(store, slug, DateTime.UtcNow.AddMinutes(-1), unpublishAt: null);

        await SweepAsync(store);
        (await LoadAsync(store, slug, id)).Status.Should().Be(ContentStatus.Scheduled, "the tenant is suspended");

        await SetStatusOkAsync(slug, "Active");
        await SweepAsync(store);
        (await LoadAsync(store, slug, id)).Status.Should().Be(ContentStatus.Published, "the tenant is active again");
    }

    [Fact]
    public async Task A_publish_whose_window_closed_while_paused_is_archived_without_ever_being_published()
    {
        var (slug, store) = await TenantWithStoreAsync();
        await SetStatusOkAsync(slug, "Suspended");
        var missed = await ScheduledAsync(store, slug, DateTime.UtcNow.AddMinutes(-10), DateTime.UtcNow.AddMinutes(-5));
        var open = await ScheduledAsync(store, slug, DateTime.UtcNow.AddMinutes(-10), DateTime.UtcNow.AddHours(1));

        await SetStatusOkAsync(slug, "Active");
        await SweepAsync(store);

        var closed = await LoadAsync(store, slug, missed);
        closed.Status.Should().Be(ContentStatus.Archived);
        closed.ScheduledPublishAt.Should().BeNull();
        closed.ScheduledUnpublishAt.Should().BeNull();

        await using (var session = store.QuerySession(slug))
        {
            var events = await session.Events.FetchStreamAsync(missed, token: Ct);
            var statuses = events.Select(e => e.Data).OfType<ContentStatusChanged>().Select(e => e.NewStatus).ToList();
            statuses.Should().Equal([ContentStatus.Archived], "the entry never went live, so no Published workflow ran for it");
        }

        var stillOpen = await LoadAsync(store, slug, open);
        stillOpen.Status.Should().Be(ContentStatus.Published, "its window is still open, so it publishes on reactivation");
        stillOpen.ScheduledUnpublishAt.Should().NotBeNull("its unpublish time is still to come");
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "migrations")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("the test binary should sit under the repository");
        return directory!.FullName;
    }
}
