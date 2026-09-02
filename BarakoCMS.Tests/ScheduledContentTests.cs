using System.Text.Json;
using Xunit;
using FluentAssertions;
using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using barakoCMS.Models;
using barakoCMS.Infrastructure.Services;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using CreateContentRequest = barakoCMS.Features.Content.Create.Request;
using CreateContentResponse = barakoCMS.Features.Content.Create.Response;

namespace BarakoCMS.Tests;

/// <summary>
/// The scheduled publish/unpublish sweep. Drives ScheduledContentService.SweepTenantAsync directly
/// (no timer) against the default-tenant session, then asserts the read model flipped and public
/// delivery reflects it. Covers: due Draft -> Published, due Published -> Archived, future schedules
/// left untouched, and the consumed field being cleared while the opposite one is preserved.
/// </summary>
[Collection("Sequential")]
public class ScheduledContentTests
{
    private readonly IntegrationTestFixture _factory;

    public ScheduledContentTests(IntegrationTestFixture factory) => _factory = factory;

    private IDocumentSession NewSession() =>
        _factory.Services.CreateScope().ServiceProvider.GetRequiredService<IDocumentSession>();

    private static Content Doc(string type, string slug, ContentStatus status,
        DateTime? publishAt = null, DateTime? unpublishAt = null) => new()
    {
        Id = Guid.NewGuid(),
        ContentType = type,
        Status = status,
        Sensitivity = SensitivityLevel.Public,
        ScheduledPublishAt = publishAt,
        ScheduledUnpublishAt = unpublishAt,
        Data = new() { ["Title"] = slug, ["Slug"] = slug },
    };

    private async Task SeedTypeAsync(string type)
    {
        using var scope = _factory.Services.CreateScope();
        var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        s.Store(new ContentTypeDefinition
        {
            IsPubliclyDeliverable = true,
            Id = Guid.NewGuid(), Name = type, DisplayName = type,
            Fields = new()
            {
                new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string" },
                new FieldDefinition { Name = "Slug", DisplayName = "Slug", Type = "slug" },
            },
        });
        await s.SaveChangesAsync();
    }

    [Fact]
    public async Task DueDraft_IsPublished_AndPublishFieldCleared()
    {
        var type = "sched_a"; await SeedTypeAsync(type);
        var past = DateTime.UtcNow.AddMinutes(-5);
        var doc = Doc(type, "due-draft", ContentStatus.Draft, publishAt: past);
        using (var s = NewSession()) { s.Store(doc); await s.SaveChangesAsync(); }

        int flipped;
        using (var s = NewSession()) flipped = await ScheduledContentService.SweepTenantAsync(s, DateTime.UtcNow, default);

        flipped.Should().Be(1);
        using var check = NewSession();
        var after = await check.LoadAsync<Content>(doc.Id);
        after!.Status.Should().Be(ContentStatus.Published);
        after.ScheduledPublishAt.Should().BeNull("the consumed schedule field is cleared");
    }

    /// <summary>
    /// A session on a tenant of this test's own. The sweep is tenant-wide rather than type-wide, so
    /// counting transitions on the default partition would count whatever every other test left
    /// scheduled there. This slug is never registered as a Tenant, so the hosted sweeper running on
    /// its timer inside the test host does not visit it either.
    /// </summary>
    private IDocumentSession TenantSession(string slug) =>
        _factory.Services.GetRequiredService<IDocumentStore>().LightweightSession(slug);

    private async Task<List<Content>> SeedDueDraftsAsync(string tenant, string prefix, int count)
    {
        var docs = Enumerable.Range(0, count)
            .Select(i => Doc("sched_batch", $"{prefix}-{i}", ContentStatus.Draft,
                publishAt: DateTime.UtcNow.AddMinutes(-5)))
            .ToList();

        await using var s = TenantSession(tenant);
        foreach (var d in docs) s.Store(d);
        await s.SaveChangesAsync();
        return docs;
    }

    private async Task<List<Content>> ReloadAsync(string tenant, List<Content> docs)
    {
        await using var s = TenantSession(tenant);
        var loaded = new List<Content>();
        foreach (var d in docs) loaded.Add((await s.LoadAsync<Content>(d.Id))!);
        return loaded;
    }

    /// <summary>
    /// One sweep applies at most batchSize * maxBatches transitions and leaves the rest for the next
    /// tick, so the memory and the transaction size are properties of the code rather than of how
    /// long the service was switched off (#127).
    /// </summary>
    [Fact]
    public async Task A_sweep_stops_at_its_batch_cap_and_leaves_the_rest_for_the_next_tick()
    {
        const string tenant = "schedbatchcap";
        var docs = await SeedDueDraftsAsync(tenant, "capped", 5);

        int flipped;
        await using (var s = TenantSession(tenant))
            flipped = await ScheduledContentService.SweepTenantAsync(s, DateTime.UtcNow, null, batchSize: 2, maxBatches: 1, default);

        flipped.Should().Be(2, "one batch of two, then the cap");

        var after = await ReloadAsync(tenant, docs);
        after.Should().HaveCount(5);
        after.Count(c => c.Status == ContentStatus.Published).Should().Be(2);
        after.Count(c => c.Status == ContentStatus.Draft).Should().Be(3,
            "the unswept remainder is still due, and the next tick a minute later picks it up");
    }

    /// <summary>
    /// The positive control for the cap. A sweep that stopped after one batch and never came back
    /// would satisfy the test above and quietly stop publishing anything past the first batch.
    /// </summary>
    [Fact]
    public async Task The_default_sweep_drains_more_than_one_batch()
    {
        const string tenant = "schedbatchdrain";
        var docs = await SeedDueDraftsAsync(tenant, "drained", 5);

        int flipped;
        await using (var s = TenantSession(tenant))
            flipped = await ScheduledContentService.SweepTenantAsync(s, DateTime.UtcNow, null, batchSize: 2, maxBatches: 25, default);

        flipped.Should().Be(5, "three batches of two, two and one, and then nothing is due");

        var after = await ReloadAsync(tenant, docs);
        after.Should().OnlyContain(c => c.Status == ContentStatus.Published);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(-1, 1)]
    public async Task A_batch_size_or_cap_below_one_is_refused(int batchSize, int maxBatches)
    {
        using var s = NewSession();

        var act = async () => await ScheduledContentService.SweepTenantAsync(
            s, DateTime.UtcNow, null, batchSize, maxBatches, default);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>(
            "a zero batch size would query for nothing forever and a zero cap would sweep nothing at all");
    }

    [Fact]
    public async Task FutureDraft_StaysDraft()
    {
        var type = "sched_b"; await SeedTypeAsync(type);
        var future = DateTime.UtcNow.AddHours(2);
        var doc = Doc(type, "future-draft", ContentStatus.Draft, publishAt: future);
        using (var s = NewSession()) { s.Store(doc); await s.SaveChangesAsync(); }

        int flipped;
        using (var s = NewSession()) flipped = await ScheduledContentService.SweepTenantAsync(s, DateTime.UtcNow, default);

        flipped.Should().Be(0);
        using var check = NewSession();
        var after = await check.LoadAsync<Content>(doc.Id);
        after!.Status.Should().Be(ContentStatus.Draft);
        after.ScheduledPublishAt.Should().NotBeNull();
    }

    [Fact]
    public async Task DuePublished_IsArchived_AndUnpublishFieldCleared()
    {
        var type = "sched_c"; await SeedTypeAsync(type);
        var past = DateTime.UtcNow.AddMinutes(-1);
        var doc = Doc(type, "expiring", ContentStatus.Published, unpublishAt: past);
        using (var s = NewSession()) { s.Store(doc); await s.SaveChangesAsync(); }

        using (var s = NewSession()) await ScheduledContentService.SweepTenantAsync(s, DateTime.UtcNow, default);

        using var check = NewSession();
        var after = await check.LoadAsync<Content>(doc.Id);
        after!.Status.Should().Be(ContentStatus.Archived);
        after.ScheduledUnpublishAt.Should().BeNull();
    }

    [Fact]
    public async Task PublishingDraft_PreservesFutureUnpublishTime()
    {
        var type = "sched_d"; await SeedTypeAsync(type);
        var doc = Doc(type, "windowed", ContentStatus.Draft,
            publishAt: DateTime.UtcNow.AddMinutes(-1), unpublishAt: DateTime.UtcNow.AddDays(7));
        using (var s = NewSession()) { s.Store(doc); await s.SaveChangesAsync(); }

        using (var s = NewSession()) await ScheduledContentService.SweepTenantAsync(s, DateTime.UtcNow, default);

        using var check = NewSession();
        var after = await check.LoadAsync<Content>(doc.Id);
        after!.Status.Should().Be(ContentStatus.Published);
        after.ScheduledPublishAt.Should().BeNull("publish time consumed");
        after.ScheduledUnpublishAt.Should().NotBeNull("the future unpublish window stays armed");
    }

    /// <summary>
    /// An item whose publish time has passed is delivered once a sweep has run.
    /// </summary>
    /// <remarks>
    /// The "before" check asserts on the scheduled item again, which is the stronger claim: that an
    /// item due for publishing is still not delivered until something publishes it.
    ///
    /// It was weakened to an unscheduled Draft for a while, because the hosted ScheduledContentService
    /// ran on a timer inside the test host and could publish the scheduled item between the seed and
    /// the read. That service is no longer registered in the fixture (#424), so the only sweep that
    /// happens here is the one this test performs, and the assertion can say what it means again.
    ///
    /// The unscheduled Draft stays as a second assertion: a sweep publishes what was scheduled and
    /// nothing else.
    /// </remarks>
    [Fact]
    public async Task ScheduledItem_AppearsInPublicDeliveryOnceDue()
    {
        var type = "sched_e"; await SeedTypeAsync(type);
        var doc = Doc(type, "goes-live", ContentStatus.Draft, publishAt: DateTime.UtcNow.AddMinutes(-2));
        var neverScheduled = Doc(type, "stays-draft", ContentStatus.Draft);
        using (var s = NewSession()) { s.Store(doc); s.Store(neverScheduled); await s.SaveChangesAsync(); }

        var anon = _factory.CreateClient();
        var before = await anon.GetStringAsync($"/api/public/{type}");
        before.Should().NotContain("goes-live", "a due item is not delivered until a sweep publishes it");
        before.Should().NotContain("stays-draft", "a Draft is not delivered");

        using (var s = NewSession()) await ScheduledContentService.SweepTenantAsync(s, DateTime.UtcNow, default);

        var after = await anon.GetStringAsync($"/api/public/{type}");
        after.Should().Contain("goes-live", "once published by the sweep it is delivered");
        after.Should().NotContain("stays-draft", "a sweep publishes what was scheduled and nothing else");
    }

    [Fact]
    public async Task ScheduleEndpoint_ArmsTimes_AndRejectsInvertedWindow()
    {
        var (token, _) = await TestHelpers.CreateAdminUserAsync(_factory);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var createRes = await client.PostAsJsonAsync("/api/contents", new CreateContentRequest
        {
            ContentType = "Article",
            Data = new Dictionary<string, object> { { "Title", "Scheduled Article" } },
        });
        createRes.IsSuccessStatusCode.Should().BeTrue();
        var contentId = (await createRes.Content.ReadFromJsonAsync<CreateContentResponse>())!.Id;

        var publishAt = DateTime.UtcNow.AddDays(1);
        var unpublishAt = DateTime.UtcNow.AddDays(8);

        // Valid window is accepted and persisted.
        var ok = await client.PutAsJsonAsync($"/api/contents/{contentId}/schedule",
            new barakoCMS.Features.Content.Schedule.Request
            {
                Id = contentId, ScheduledPublishAt = publishAt, ScheduledUnpublishAt = unpublishAt,
            });
        ok.StatusCode.Should().Be(HttpStatusCode.OK);

        // Assert persistence via a Marten session load (Kind-consistent, avoids JSON UTC/local drift).
        using (var check = NewSession())
        {
            var loaded = await check.LoadAsync<Content>(contentId);
            loaded!.ScheduledPublishAt.Should().NotBeNull();
            // Stored ticks are UTC; force-interpret the loaded Kind as UTC so the compare ignores any
            // Unspecified/Local Kind the serializer round-trip may hand back.
            DateTime.SpecifyKind(loaded.ScheduledPublishAt!.Value, DateTimeKind.Utc)
                .Should().BeCloseTo(publishAt, TimeSpan.FromSeconds(2));
            DateTime.SpecifyKind(loaded.ScheduledUnpublishAt!.Value, DateTimeKind.Utc)
                .Should().BeCloseTo(unpublishAt, TimeSpan.FromSeconds(2));
        }

        // Unpublish before publish is rejected.
        var bad = await client.PutAsJsonAsync($"/api/contents/{contentId}/schedule",
            new barakoCMS.Features.Content.Schedule.Request
            {
                Id = contentId,
                ScheduledPublishAt = DateTime.UtcNow.AddDays(5),
                ScheduledUnpublishAt = DateTime.UtcNow.AddDays(2),
            });
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// What is armed can be read back.
    /// </summary>
    /// <remarks>
    /// The schedule endpoint had no way to answer "when is this going out", so the only way to know
    /// an arming took was to wait and see whether it happened. The admin needs it to show anything
    /// at all, and adding a field to a response is not a breaking change, so it can go in a major or
    /// out of one.
    ///
    /// The zone is asserted because the document stores DateTime and the response is
    /// DateTimeOffset. An implicit conversion reads an Unspecified Kind as local time, which is the
    /// silent-by-the-server's-offset failure that DateWireFormatTests exists for. That sweep walks
    /// this endpoint too, but only sees fields that are populated, and nothing else populates these.
    /// </remarks>
    [Fact]
    public async Task GetContent_ReportsWhatIsScheduled_WithAZone()
    {
        var (token, _) = await TestHelpers.CreateAdminUserAsync(_factory);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var createRes = await client.PostAsJsonAsync("/api/contents", new CreateContentRequest
        {
            ContentType = "Article",
            Data = new Dictionary<string, object> { { "Title", "Readable schedule" } },
        });
        createRes.IsSuccessStatusCode.Should().BeTrue();
        var contentId = (await createRes.Content.ReadFromJsonAsync<CreateContentResponse>())!.Id;

        var before = await client.GetFromJsonAsync<JsonElement>($"/api/contents/{contentId}");
        before.GetProperty("scheduledPublishAt").ValueKind.Should().Be(JsonValueKind.Null,
            "nothing is armed on a new entry, and null says so more clearly than an absent key");

        var publishAt = new DateTime(2027, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        var arm = await client.PutAsJsonAsync($"/api/contents/{contentId}/schedule",
            new barakoCMS.Features.Content.Schedule.Request
            {
                Id = contentId, ScheduledPublishAt = publishAt,
            });
        arm.StatusCode.Should().Be(HttpStatusCode.OK);

        var after = await client.GetFromJsonAsync<JsonElement>($"/api/contents/{contentId}");
        var raw = after.GetProperty("scheduledPublishAt").GetString();

        raw.Should().NotBeNull();
        DateTimeOffset.Parse(raw!, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind)
            .Should().Be(new DateTimeOffset(publishAt),
                "the instant that comes back is the instant that was armed, wherever the server is");

        after.GetProperty("scheduledUnpublishAt").ValueKind.Should().Be(JsonValueKind.Null,
            "arming one time does not arm the other");
    }

}
