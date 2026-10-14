using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Infrastructure.Jobs;
using barakoCMS.Models;
using Xunit;

namespace BarakoCMS.Tests.Features.ContentTypes;

/// <summary>
/// One endpoint changes a field of a type that holds entries, classifies the change, and applies
/// one that touches entries through a resumable job (#934).
/// </summary>
[Collection("Sequential")]
public class ChangeFieldTests : IAsyncLifetime
{
    private readonly IntegrationTestFixture _fixture;
    private readonly HttpClient _client;

    public ChangeFieldTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.CreateClient();
    }

    public async ValueTask InitializeAsync() =>
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", await _fixture.StoredUserTokenAsync("Admin", "SuperAdmin"));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string NewName() => "fieldchange" + Guid.NewGuid().ToString("n")[..10];

    private async Task<string> TypeWithEntriesAsync(int entries, params object[] extraFields)
    {
        var name = NewName();
        var res = await _client.PostAsJsonAsync("/api/content-types", new
        {
            name, displayName = name,
            fields = new object[] { new { name = "Title", type = "string" }, new { name = "Doctor", type = "string" } }
                .Concat(extraFields).ToArray(),
        }, Ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));

        for (var i = 0; i < entries; i++)
        {
            var created = await _client.PostAsJsonAsync("/api/contents", new
            {
                contentType = name,
                data = new Dictionary<string, object> { ["Title"] = $"entry {i}", ["Doctor"] = $"Dr {i}" },
            }, Ct);
            created.StatusCode.Should().Be(HttpStatusCode.OK, await created.Content.ReadAsStringAsync(Ct));
        }

        return name;
    }

    private Task<HttpResponseMessage> ChangeAsync(string type, string field, object body) =>
        _client.PostAsJsonAsync($"/api/content-types/{type}/fields/{field}/change", body, Ct);

    private async Task<JsonElement> CompletedAsync(Guid id)
    {
        for (var i = 0; i < 150; i++)
        {
            var status = await (await _client.GetAsync($"/api/content-types/field-changes/{id}", Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
            if (status.GetProperty("state").GetString() == "completed")
            {
                return status;
            }

            await Task.Delay(200, Ct);
        }

        throw new Xunit.Sdk.XunitException($"Field change {id} did not complete.");
    }

    private async Task<List<Content>> EntriesAsync(string type)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return (await session.Query<Content>().Where(c => c.ContentType == type).ToListAsync(Ct)).ToList();
    }

    private async Task<ContentTypeDefinition> DefinitionAsync(string type)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return await session.Query<ContentTypeDefinition>().SingleAsync(d => d.Name == type, Ct);
    }

    [Fact]
    public async Task Renaming_a_field_moves_every_entrys_value_to_the_new_name_through_a_job()
    {
        var type = await TypeWithEntriesAsync(3);

        var res = await ChangeAsync(type, "Doctor", new { newName = "Practitioner" });

        res.StatusCode.Should().Be(HttpStatusCode.Accepted, await res.Content.ReadAsStringAsync(Ct));
        var body = await res.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("classification").GetString().Should().Be("rename");
        var status = await CompletedAsync(body.GetProperty("change").GetProperty("id").GetGuid());
        status.GetProperty("total").GetInt32().Should().Be(3);
        status.GetProperty("processed").GetInt32().Should().Be(3, "an entry is counted once, though a rename touches it twice");

        var entries = await EntriesAsync(type);
        entries.Should().HaveCount(3);
        entries.Should().OnlyContain(e => e.Data.ContainsKey("Practitioner") && !e.Data.ContainsKey("Doctor"));
        entries.Select(e => e.Data["Practitioner"]!.ToString()).Should().BeEquivalentTo(["Dr 0", "Dr 1", "Dr 2"]);
        (await DefinitionAsync(type)).Fields.Select(f => f.Name).Should().Equal("Title", "Practitioner");
    }

    [Fact]
    public async Task A_rename_does_not_count_as_an_edit_of_the_entry()
    {
        var type = await TypeWithEntriesAsync(1);
        var before = (await EntriesAsync(type)).Single();

        var res = await ChangeAsync(type, "Doctor", new { newName = "Practitioner" });
        await CompletedAsync((await res.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("change").GetProperty("id").GetGuid());

        var after = (await EntriesAsync(type)).Single();
        after.UpdatedAt.Should().Be(before.UpdatedAt);
        after.LastModifiedBy.Should().Be(before.LastModifiedBy);
    }

    [Fact]
    public async Task A_label_is_additive_and_applied_at_once()
    {
        var type = await TypeWithEntriesAsync(1);

        var res = await ChangeAsync(type, "Doctor", new { displayName = "Attending" });

        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));
        var body = await res.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("classification").GetString().Should().Be("additive");
        body.GetProperty("change").ValueKind.Should().Be(JsonValueKind.Null);
        (await DefinitionAsync(type)).Fields.Single(f => f.Name == "Doctor").DisplayName.Should().Be("Attending");
    }

    [Theory]
    [InlineData("practitioner", "PascalCase")]
    [InlineData("Title", "more than once")]
    [InlineData("doctor", "only in case")]
    [InlineData("  ", "cannot be empty")]
    public async Task A_rename_the_type_could_not_hold_is_refused(string newName, string expected)
    {
        var type = await TypeWithEntriesAsync(0);

        var res = await ChangeAsync(type, "Doctor", new { newName });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync(Ct)).Should().Contain(expected);
    }

    [Fact]
    public async Task A_label_is_sent_on_its_own()
    {
        var type = await TypeWithEntriesAsync(0);

        var res = await ChangeAsync(type, "Doctor", new { newName = "Practitioner", displayName = "Practitioner" });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync(Ct)).Should().Contain("displayName on its own");
    }

    [Fact]
    public async Task Only_a_public_field_holding_no_token_is_renamed_converted_or_removed()
    {
        var type = await TypeWithEntriesAsync(1,
            new { name = "Ssn", type = "string", sensitivity = "Hidden" },
            new { name = "Code", type = "token", sensitivity = "Hidden" });

        foreach (var (field, body, expected) in new (string, object, string)[]
        {
            ("Ssn", new { newName = "Tax" }, "is Hidden"),
            ("Ssn", new { toType = "text" }, "is Hidden"),
            ("Ssn", new { remove = true, allowDestructive = true }, "is Hidden"),
            ("Code", new { newName = "Key" }, "holds a token"),
        })
        {
            var refused = await ChangeAsync(type, field, body);
            refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await refused.Content.ReadAsStringAsync(Ct)).Should().Contain(expected);
        }

        var fields = (await DefinitionAsync(type)).Fields;
        fields.Select(f => f.Name).Should().Equal("Title", "Doctor", "Ssn", "Code");
        fields.Single(f => f.Name == "Ssn").Sensitivity.Should().Be(SensitivityLevel.Hidden);

        var allowed = await ChangeAsync(type, "Doctor", new { newName = "Practitioner" });
        allowed.StatusCode.Should().Be(HttpStatusCode.Accepted, "the positive control: " + await allowed.Content.ReadAsStringAsync(Ct));
        await CompletedAsync((await allowed.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("change").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task An_unknown_type_or_field_is_not_found()
    {
        var type = await TypeWithEntriesAsync(0);

        (await ChangeAsync(type, "Nope", new { newName = "Other" })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ChangeAsync(NewName(), "Doctor", new { newName = "Other" })).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private async Task<string> GradesAsync(params string[] grades)
    {
        var type = await TypeWithEntriesAsync(0, new { name = "Grade", type = "string" });
        foreach (var grade in grades)
        {
            (await _client.PostAsJsonAsync("/api/contents", new
            {
                contentType = type, data = new Dictionary<string, object> { ["Title"] = grade, ["Grade"] = grade },
            }, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        return type;
    }

    private static readonly object[] Levels =
    [
        new { value = "beginner", label = "Beginner" },
        new { value = "advanced", label = "Advanced" },
    ];

    [Fact]
    public async Task Converting_a_string_to_a_choice_through_a_mapping_rewrites_every_value()
    {
        var type = await GradesAsync("Grade 1", "grade 1", "Grade 6");

        var res = await ChangeAsync(type, "Grade", new
        {
            toType = "choice", options = Levels,
            mapping = new Dictionary<string, string> { ["Grade 1"] = "beginner", ["grade 1"] = "beginner", ["Grade 6"] = "advanced" },
        });

        res.StatusCode.Should().Be(HttpStatusCode.Accepted, await res.Content.ReadAsStringAsync(Ct));
        var body = await res.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("classification").GetString().Should().Be("convert");
        await CompletedAsync(body.GetProperty("change").GetProperty("id").GetGuid());

        var grades = (await EntriesAsync(type)).Select(e => e.Data["Grade"]!.ToString()).ToList();
        grades.Should().HaveCount(3);
        grades.Should().BeEquivalentTo(["beginner", "beginner", "advanced"]);
        var field = (await DefinitionAsync(type)).Fields.Single(f => f.Name == "Grade");
        field.Type.Should().Be("choice");
        field.Options!.Select(o => o.Value).Should().Equal("beginner", "advanced");
    }

    [Fact]
    public async Task A_convert_that_drops_values_is_refused_with_the_count_unless_allowed()
    {
        var type = await GradesAsync("Grade 1", "Grade 7", "Grade 8");
        var mapping = new Dictionary<string, string> { ["Grade 1"] = "beginner" };

        var refused = await ChangeAsync(type, "Grade", new { toType = "choice", options = Levels, mapping });

        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await refused.Content.ReadAsStringAsync(Ct)).Should().Contain("drops the value 2 entries hold");
        (await DefinitionAsync(type)).Fields.Single(f => f.Name == "Grade").Type.Should().Be("string", "a refusal changes nothing");

        var allowed = await ChangeAsync(type, "Grade", new { toType = "choice", options = Levels, mapping, allowDestructive = true });
        allowed.StatusCode.Should().Be(HttpStatusCode.Accepted, await allowed.Content.ReadAsStringAsync(Ct));
        var body = await allowed.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("classification").GetString().Should().Be("destructive");
        body.GetProperty("change").GetProperty("valuesLost").GetInt32().Should().Be(2);
        await CompletedAsync(body.GetProperty("change").GetProperty("id").GetGuid());

        var entries = await EntriesAsync(type);
        entries.Should().HaveCount(3);
        entries.Count(e => e.Data.ContainsKey("Grade")).Should().Be(1);
    }

    [Fact]
    public async Task Removing_a_field_without_the_destructive_flag_is_refused_with_the_count()
    {
        var type = await TypeWithEntriesAsync(3);

        var refused = await ChangeAsync(type, "Doctor", new { remove = true });

        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await refused.Content.ReadAsStringAsync(Ct)).Should().Contain("drops the value 3 entries hold");
        (await DefinitionAsync(type)).Fields.Select(f => f.Name).Should().Contain("Doctor");

        var allowed = await ChangeAsync(type, "Doctor", new { remove = true, allowDestructive = true });
        allowed.StatusCode.Should().Be(HttpStatusCode.Accepted, await allowed.Content.ReadAsStringAsync(Ct));
        await CompletedAsync((await allowed.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("change").GetProperty("id").GetGuid());

        (await DefinitionAsync(type)).Fields.Select(f => f.Name).Should().Equal("Title");
        var entries = await EntriesAsync(type);
        entries.Should().HaveCount(3);
        entries.Should().OnlyContain(e => !e.Data.ContainsKey("Doctor"));
    }

    [Theory]
    [InlineData("""{ "toType": "geopoint" }""", "converts to one of")]
    [InlineData("""{ "toType": "choice" }""", "options")]
    [InlineData("""{ "newName": "Other", "remove": true }""", "one change at a time")]
    public async Task A_convert_the_field_cannot_take_is_refused(string json, string expected)
    {
        var type = await TypeWithEntriesAsync(0);

        var res = await _client.PostAsync($"/api/content-types/{type}/fields/Doctor/change",
            new StringContent(json, System.Text.Encoding.UTF8, "application/json"), Ct);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync(Ct)).Should().Contain(expected);
    }

    /// <summary>A change stored and not queued, as one whose job stopped before it ran.</summary>
    private async Task<Guid> StalledRenameAsync(string type)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var change = new FieldChange
        {
            Id = Guid.NewGuid(), ContentType = type, Field = "Doctor", Kind = FieldChangeKind.Rename,
            NewName = "Practitioner", State = FieldChangeState.Running, Total = 5,
            StartedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        session.Store(change);
        await session.SaveChangesAsync(Ct);
        return change.Id;
    }

    [Fact]
    public async Task A_change_stopped_part_way_reports_its_progress_and_resuming_it_finishes_it()
    {
        var type = await TypeWithEntriesAsync(5);
        var id = await StalledRenameAsync(type);
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        var configuration = _fixture.Services.GetRequiredService<IConfiguration>();

        await FieldChanges.RunAsync(store, configuration, "*DEFAULT*", id, maxBatches: 1, Ct, batchSize: 2);

        var partway = await (await _client.GetAsync($"/api/content-types/field-changes/{id}", Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        partway.GetProperty("state").GetString().Should().Be("running");
        partway.GetProperty("step").GetInt32().Should().Be(0);
        partway.GetProperty("processed").GetInt32().Should().Be(0, "a rename counts an entry when its old key goes, in the last step");
        (await EntriesAsync(type)).Count(e => e.Data.ContainsKey("Practitioner")).Should().Be(2);
        (await DefinitionAsync(type)).Fields.Select(f => f.Name).Should().Contain("Doctor", "the field is read under its old name until the change completes");

        var resumed = await _client.PostAsync($"/api/content-types/field-changes/{id}/resume", null, Ct);
        resumed.StatusCode.Should().Be(HttpStatusCode.Accepted, await resumed.Content.ReadAsStringAsync(Ct));
        await CompletedAsync(id);

        var entries = await EntriesAsync(type);
        entries.Should().HaveCount(5);
        entries.Should().OnlyContain(e => e.Data.ContainsKey("Practitioner") && !e.Data.ContainsKey("Doctor"));
        (await _client.PostAsync($"/api/content-types/field-changes/{id}/resume", null, Ct)).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Entry_writes_to_a_type_wait_while_a_change_runs()
    {
        var type = await TypeWithEntriesAsync(1);
        var id = await StalledRenameAsync(type);

        var write = await _client.PostAsJsonAsync("/api/contents", new
        {
            contentType = type, data = new Dictionary<string, object> { ["Title"] = "late" },
        }, Ct);
        write.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await write.Content.ReadAsStringAsync(Ct)).Should().Contain("field change running");

        (await ChangeAsync(type, "Title", new { newName = "Heading" })).StatusCode.Should().Be(HttpStatusCode.Conflict, "one change at a time");

        await _client.PostAsync($"/api/content-types/field-changes/{id}/resume", null, Ct);
        await CompletedAsync(id);
        (await _client.PostAsJsonAsync("/api/contents", new
        {
            contentType = type, data = new Dictionary<string, object> { ["Title"] = "late" },
        }, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Edits_to_the_types_definition_wait_while_a_change_runs()
    {
        var type = await TypeWithEntriesAsync(1);
        var id = await StalledRenameAsync(type);

        var sensitivity = await _client.PutAsJsonAsync($"/api/content-types/{type}/fields/Title/sensitivity", new { sensitivity = "Hidden" }, Ct);
        sensitivity.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await sensitivity.Content.ReadAsStringAsync(Ct)).Should().Contain("field change running");
        (await _client.PostAsJsonAsync($"/api/content-types/{type}/fields", new { fieldName = "Notes", type = "text" }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await _client.PutAsJsonAsync($"/api/content-types/{type}/numbering", new { field = "Title" }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await DefinitionAsync(type)).Fields.Single(f => f.Name == "Title").Sensitivity.Should().Be(SensitivityLevel.Public);

        await _client.PostAsync($"/api/content-types/field-changes/{id}/resume", null, Ct);
        await CompletedAsync(id);
        var added = await _client.PostAsJsonAsync($"/api/content-types/{type}/fields", new { fieldName = "Notes", type = "text" }, Ct);
        added.StatusCode.Should().Be(HttpStatusCode.OK, "the positive control: " + await added.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Resuming_a_change_whose_job_is_still_queued_is_refused()
    {
        var type = await TypeWithEntriesAsync(1);
        var id = await StalledRenameAsync(type);
        var queued = new JobRecord
        {
            TrackingID = Guid.NewGuid(), QueueID = "not-a-queue", CommandType = typeof(FieldChangeCommand).FullName!,
            CommandJson = JsonSerializer.Serialize(new { TenantId = "*DEFAULT*", ChangeId = id }),
            State = JobState.Pending, CreatedAt = DateTime.UtcNow,
            ExecuteAfter = DateTime.UtcNow.AddHours(1), DequeueAfter = DateTime.UtcNow.AddHours(1), ExpireOn = DateTime.UtcNow.AddHours(2),
        };
        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(queued);
            await session.SaveChangesAsync(Ct);
        }

        var refused = await _client.PostAsync($"/api/content-types/field-changes/{id}/resume", null, Ct);
        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await refused.Content.ReadAsStringAsync(Ct)).Should().Contain("still queued or running");

        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Delete<JobRecord>(queued.TrackingID);
            await session.SaveChangesAsync(Ct);
        }

        (await _client.PostAsync($"/api/content-types/field-changes/{id}/resume", null, Ct)).StatusCode.Should().Be(HttpStatusCode.Accepted);
        await CompletedAsync(id);
    }

    [Fact]
    public async Task Two_runs_of_one_change_cannot_both_save_from_the_progress_they_read()
    {
        var type = await TypeWithEntriesAsync(0);
        var id = await StalledRenameAsync(type);
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        await using var first = store.LightweightSession();
        await using var second = store.LightweightSession();
        var a = (await first.LoadAsync<FieldChange>(id, Ct))!;
        var b = (await second.LoadAsync<FieldChange>(id, Ct))!;

        a.Processed++;
        first.Store(a);
        await first.SaveChangesAsync(Ct);
        b.Processed++;
        second.Store(b);

        await FluentActions.Awaiting(() => second.SaveChangesAsync(Ct)).Should().ThrowAsync<JasperFx.ConcurrencyException>();
    }

    [Fact]
    public async Task Another_tenants_change_is_not_found()
    {
        var type = await TypeWithEntriesAsync(1);
        var id = await StalledRenameAsync(type);
        (await _client.GetAsync($"/api/content-types/field-changes/{id}", Ct)).StatusCode.Should().Be(HttpStatusCode.OK, "the positive control");

        var slug = await Features.Tenants.TenantProfileSeed.TenantAsync(_fixture, Ct);
        var other = await Features.Tenants.TenantProfileSeed.AdminInAsync(_fixture, slug, Ct);
        (await other.GetAsync($"/api/content-types/field-changes/{id}", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await other.PostAsync($"/api/content-types/field-changes/{id}/resume", null, Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        await _client.PostAsync($"/api/content-types/field-changes/{id}/resume", null, Ct);
        await CompletedAsync(id);
    }

    [Fact]
    public async Task A_sensitivity_change_on_a_type_past_one_batch_runs_on_the_job_queue_and_takes_the_value_out_of_search_text()
    {
        var type = await TypeWithEntriesAsync(FieldChanges.BatchSize + 1);

        var res = await _client.PutAsJsonAsync($"/api/content-types/{type}/fields/Doctor/sensitivity", new { sensitivity = "Hidden" }, Ct);

        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));
        var change = (await res.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("change");
        change.GetProperty("kind").GetString().Should().Be("sensitivity");
        change.GetProperty("total").GetInt32().Should().Be(FieldChanges.BatchSize + 1);
        var done = await CompletedAsync(change.GetProperty("id").GetGuid());
        done.GetProperty("steps").GetInt32().Should().Be(2);

        (await DefinitionAsync(type)).Fields.Single(f => f.Name == "Doctor").Sensitivity.Should().Be(SensitivityLevel.Hidden);
        var entries = await EntriesAsync(type);
        entries.Should().HaveCount(FieldChanges.BatchSize + 1);
        var indexed = entries.Where(e => e.SearchText is not null).ToList();
        indexed.Should().HaveCount(FieldChanges.BatchSize + 1);
        indexed.Should().OnlyContain(e => !e.SearchText!.Contains("Dr "));
    }

    [Fact]
    public async Task A_sensitivity_change_on_a_small_type_is_applied_before_the_answer()
    {
        var type = await TypeWithEntriesAsync(2);

        var res = await _client.PutAsJsonAsync($"/api/content-types/{type}/fields/Doctor/sensitivity", new { sensitivity = "Hidden" }, Ct);

        var body = await res.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("change").GetProperty("state").GetString().Should().Be("completed");
        (await DefinitionAsync(type)).Fields.Single(f => f.Name == "Doctor").Sensitivity.Should().Be(SensitivityLevel.Hidden);
    }

    [Fact]
    public async Task Changing_a_field_needs_manage_content_types()
    {
        var type = await TypeWithEntriesAsync(0);
        (await ChangeAsync(type, "Doctor", new { displayName = "Doc" })).StatusCode.Should().Be(HttpStatusCode.OK, "the positive control");

        var anonymous = _fixture.CreateClient();
        (await anonymous.PostAsJsonAsync($"/api/content-types/{type}/fields/Doctor/change", new { displayName = "x" }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var user = _fixture.CreateClient();
        user.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", await _fixture.StoredUserTokenAsync("User"));
        (await user.PostAsJsonAsync($"/api/content-types/{type}/fields/Doctor/change", new { displayName = "x" }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await user.GetAsync($"/api/content-types/field-changes/{Guid.NewGuid()}", Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await user.PostAsync($"/api/content-types/field-changes/{Guid.NewGuid()}/resume", null, Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
