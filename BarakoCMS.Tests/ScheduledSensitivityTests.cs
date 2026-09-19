using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using CreateContentRequest = barakoCMS.Features.Content.Create.Request;
using CreateContentResponse = barakoCMS.Features.Content.Create.Response;
using ScheduleRequest = barakoCMS.Features.Content.Schedule.Request;

namespace BarakoCMS.Tests;

/// <summary>
/// A sensitivity change armed for a moment (#824): the schedule endpoint stores the intent, the
/// sweep applies it as a real ContentSensitivityChanged, and the entry stays Published throughout.
/// </summary>
[Collection("Sequential")]
public class ScheduledSensitivityTests
{
    private readonly IntegrationTestFixture _factory;

    public ScheduledSensitivityTests(IntegrationTestFixture factory) => _factory = factory;

    private IDocumentSession NewSession() =>
        _factory.Services.CreateScope().ServiceProvider.GetRequiredService<IDocumentSession>();

    [Fact]
    public async Task A_due_sensitivity_change_is_applied_and_the_entry_stays_published()
    {
        var type = await SeedTypeAsync("sens_due");
        var doc = Doc(type, "goes-hidden", SensitivityLevel.Public,
            scheduled: SensitivityLevel.Hidden, at: DateTime.UtcNow.AddMinutes(-2));
        using (var s = NewSession()) { s.Store(doc); await s.SaveChangesAsync(); }

        var anonymous = _factory.CreateClient();
        (await anonymous.GetAsync($"/api/public/{type}/goes-hidden")).StatusCode
            .Should().Be(HttpStatusCode.OK, "the control: before the sweep the entry is public");

        int applied;
        using (var s = NewSession()) applied = await ScheduledContentService.SweepTenantAsync(s, DateTime.UtcNow, default);

        applied.Should().Be(1);
        using var check = NewSession();
        var after = await check.LoadAsync<Content>(doc.Id);
        after!.Sensitivity.Should().Be(SensitivityLevel.Hidden);
        after.Status.Should().Be(ContentStatus.Published, "a sensitivity schedule is not an unpublish");
        after.ScheduledSensitivity.Should().BeNull("the consumed schedule is cleared");
        after.ScheduledSensitivityAt.Should().BeNull();
        after.LastModifiedBy.Should().Be(ScheduledContentService.SystemActor);

        (await anonymous.GetAsync($"/api/public/{type}/goes-hidden")).StatusCode
            .Should().Be(HttpStatusCode.NotFound, "delivery reads the document's sensitivity on the next request");
    }

    [Fact]
    public async Task A_due_change_can_lower_sensitivity_back_to_public()
    {
        var type = await SeedTypeAsync("sens_lower");
        var doc = Doc(type, "comes-back", SensitivityLevel.Hidden,
            scheduled: SensitivityLevel.Public, at: DateTime.UtcNow.AddMinutes(-1));
        using (var s = NewSession()) { s.Store(doc); await s.SaveChangesAsync(); }

        using (var s = NewSession()) (await ScheduledContentService.SweepTenantAsync(s, DateTime.UtcNow, default)).Should().Be(1);

        using var check = NewSession();
        (await check.LoadAsync<Content>(doc.Id))!.Sensitivity.Should().Be(SensitivityLevel.Public);
        (await _factory.CreateClient().GetAsync($"/api/public/{type}/comes-back")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_future_sensitivity_change_is_left_alone()
    {
        var type = await SeedTypeAsync("sens_future");
        var doc = Doc(type, "not-yet", SensitivityLevel.Public,
            scheduled: SensitivityLevel.Hidden, at: DateTime.UtcNow.AddHours(1));
        using (var s = NewSession()) { s.Store(doc); await s.SaveChangesAsync(); }

        using (var s = NewSession()) (await ScheduledContentService.SweepTenantAsync(s, DateTime.UtcNow, default)).Should().Be(0);

        using var check = NewSession();
        var after = await check.LoadAsync<Content>(doc.Id);
        after!.Sensitivity.Should().Be(SensitivityLevel.Public);
        after.ScheduledSensitivity.Should().Be(SensitivityLevel.Hidden, "the schedule is still armed");
    }

    /// <summary>
    /// A publish time and a sensitivity time due on the same tick are both applied in one save, and
    /// the clearing events leave the other schedule fields as they were.
    /// </summary>
    [Fact]
    public async Task An_entry_due_to_publish_and_to_change_sensitivity_gets_both_in_one_sweep()
    {
        var type = await SeedTypeAsync("sens_both");
        var unpublishAt = DateTime.UtcNow.AddDays(3);
        var doc = Doc(type, "both", SensitivityLevel.Public,
            scheduled: SensitivityLevel.Sensitive, at: DateTime.UtcNow.AddMinutes(-1));
        doc.Status = ContentStatus.Scheduled;
        doc.ScheduledPublishAt = DateTime.UtcNow.AddMinutes(-1);
        doc.ScheduledUnpublishAt = unpublishAt;
        using (var s = NewSession()) { s.Store(doc); await s.SaveChangesAsync(); }

        using (var s = NewSession()) (await ScheduledContentService.SweepTenantAsync(s, DateTime.UtcNow, default)).Should().Be(1);

        using var check = NewSession();
        var after = await check.LoadAsync<Content>(doc.Id);
        after!.Status.Should().Be(ContentStatus.Published);
        after.Sensitivity.Should().Be(SensitivityLevel.Sensitive);
        after.ScheduledPublishAt.Should().BeNull();
        after.ScheduledSensitivity.Should().BeNull();
        DateTime.SpecifyKind(after.ScheduledUnpublishAt!.Value, DateTimeKind.Utc)
            .Should().BeCloseTo(unpublishAt, TimeSpan.FromSeconds(2), "the unpublish time was not due and stays armed");
    }

    [Fact]
    public async Task The_schedule_endpoint_arms_a_change_reads_it_back_and_records_it_in_the_history()
    {
        var client = await AdminAsync();
        var contentId = await CreateArticleAsync(client, "Sensitivity later");

        var at = new DateTime(2027, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        var arm = await client.PutAsJsonAsync($"/api/contents/{contentId}/schedule", new ScheduleRequest
        {
            Id = contentId, ScheduledSensitivity = SensitivityLevel.Hidden, ScheduledSensitivityAt = at,
        });
        arm.StatusCode.Should().Be(HttpStatusCode.OK);
        var armed = await arm.Content.ReadFromJsonAsync<JsonElement>();
        armed.GetProperty("scheduledSensitivity").GetString().Should().Be(nameof(SensitivityLevel.Hidden));

        var read = await client.GetFromJsonAsync<JsonElement>($"/api/contents/{contentId}");
        read.GetProperty("scheduledSensitivity").GetString().Should().Be(nameof(SensitivityLevel.Hidden));
        DateTimeOffset.Parse(read.GetProperty("scheduledSensitivityAt").GetString()!,
                System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind)
            .Should().Be(new DateTimeOffset(at), "the instant that comes back is the instant that was armed, with a zone");
        read.GetProperty("sensitivity").GetString().Should().Be(nameof(SensitivityLevel.Public), "arming is not applying");

        var history = await client.GetFromJsonAsync<JsonElement>($"/api/contents/{contentId}/history");
        var types = history.GetProperty("items").EnumerateArray()
            .Select(v => v.GetProperty("changeType").GetString()).ToList();
        types.Should().NotBeEmpty();
        types.Should().Contain("SensitivityScheduled", "arming is a decision somebody took, and the history says who");

        var clear = await client.PutAsJsonAsync($"/api/contents/{contentId}/schedule", new ScheduleRequest { Id = contentId });
        clear.StatusCode.Should().Be(HttpStatusCode.OK);
        var cleared = await client.GetFromJsonAsync<JsonElement>($"/api/contents/{contentId}");
        cleared.GetProperty("scheduledSensitivity").ValueKind.Should().Be(JsonValueKind.Null, "a request with neither field clears it");
    }

    [Fact]
    public async Task The_schedule_endpoint_refuses_a_past_time_a_lone_field_and_the_current_level()
    {
        var client = await AdminAsync();
        var contentId = await CreateArticleAsync(client, "Refused schedules");

        var past = await client.PutAsJsonAsync($"/api/contents/{contentId}/schedule", new ScheduleRequest
        {
            Id = contentId, ScheduledSensitivity = SensitivityLevel.Hidden, ScheduledSensitivityAt = DateTime.UtcNow.AddMinutes(-1),
        });
        past.StatusCode.Should().Be(HttpStatusCode.BadRequest, "a change wanted now is made now, by the person deciding it");

        var lone = await client.PutAsJsonAsync($"/api/contents/{contentId}/schedule", new ScheduleRequest
        {
            Id = contentId, ScheduledSensitivityAt = DateTime.UtcNow.AddDays(1),
        });
        lone.StatusCode.Should().Be(HttpStatusCode.BadRequest, "a time with no level says nothing");

        var same = await client.PutAsJsonAsync($"/api/contents/{contentId}/schedule", new ScheduleRequest
        {
            Id = contentId, ScheduledSensitivity = SensitivityLevel.Public, ScheduledSensitivityAt = DateTime.UtcNow.AddDays(1),
        });
        same.StatusCode.Should().Be(HttpStatusCode.BadRequest, "a new entry is Public already");

        var read = await client.GetFromJsonAsync<JsonElement>($"/api/contents/{contentId}");
        read.GetProperty("scheduledSensitivity").ValueKind.Should().Be(JsonValueKind.Null, "none of the refused requests armed anything");
    }

    /// <summary>
    /// A console that knows only the publish times keeps working: its save neither clears an armed
    /// sensitivity change it cannot see nor appends a clearing event for a schedule that was never
    /// armed.
    /// </summary>
    [Fact]
    public async Task A_save_that_does_not_mention_sensitivity_leaves_no_event_when_nothing_was_armed()
    {
        var client = await AdminAsync();
        var contentId = await CreateArticleAsync(client, "Old console");

        var save = await client.PutAsJsonAsync($"/api/contents/{contentId}/schedule", new ScheduleRequest
        {
            Id = contentId, ScheduledPublishAt = DateTime.UtcNow.AddDays(1),
        });
        save.StatusCode.Should().Be(HttpStatusCode.OK);

        var history = await client.GetFromJsonAsync<JsonElement>($"/api/contents/{contentId}/history");
        var types = history.GetProperty("items").EnumerateArray()
            .Select(v => v.GetProperty("changeType").GetString()).ToList();
        types.Should().Contain("Scheduled");
        types.Should().NotContain("SensitivityScheduled", "nothing was armed and nothing was cleared");
    }

    private static Content Doc(string type, string slug, SensitivityLevel sensitivity, SensitivityLevel scheduled, DateTime at) => new()
    {
        Id = Guid.NewGuid(),
        ContentType = type,
        Status = ContentStatus.Published,
        Sensitivity = sensitivity,
        ScheduledSensitivity = scheduled,
        ScheduledSensitivityAt = at,
        Data = new() { ["Title"] = slug, ["Slug"] = slug },
    };

    private async Task<string> SeedTypeAsync(string type)
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
        return type;
    }

    private async Task<HttpClient> AdminAsync()
    {
        var (token, _) = await TestHelpers.CreateAdminUserAsync(_factory);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<Guid> CreateArticleAsync(HttpClient client, string title)
    {
        var created = await client.PostAsJsonAsync("/api/contents", new CreateContentRequest
        {
            ContentType = "Article",
            Data = new Dictionary<string, object> { { "Title", title } },
        });
        created.IsSuccessStatusCode.Should().BeTrue();
        return (await created.Content.ReadFromJsonAsync<CreateContentResponse>())!.Id;
    }
}
