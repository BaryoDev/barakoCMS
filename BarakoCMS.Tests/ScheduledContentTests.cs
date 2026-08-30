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
    /// The "before" check uses an unscheduled Draft rather than the scheduled one. ScheduledContentService
    /// is registered as a hosted service, so a sweeper is running on a timer inside the test host and
    /// can publish the scheduled item between the seed and the read. Asserting that the scheduled
    /// item is absent before the manual sweep is therefore a race, and it failed the full suite while
    /// passing every time in isolation.
    ///
    /// An unscheduled Draft is something no sweeper will ever touch, so it states the same invariant,
    /// that delivery excludes Drafts, without depending on the timing of a background service. The
    /// scheduled item is still what the second half asserts on, which is the part this test is for.
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
}
