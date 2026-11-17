using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using barakoCMS.Events;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace BarakoCMS.Tests.Features.ContentTypes;

/// <summary>
/// The dry run of each content type and entry write, side by side with the real write: the same
/// refusal, the same permission check, and no document or event left behind (#892).
/// </summary>
[Collection("Integration1")]
public class DryRunWriteTests : IAsyncLifetime
{
    private readonly IntegrationTestFixture _fixture;
    private readonly HttpClient _client;

    public DryRunWriteTests(IntegrationTestFixture fixture)
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

    private static string NewName() => "dryw" + Guid.NewGuid().ToString("n")[..10];

    private async Task<string> TypeAsync()
    {
        var name = NewName();
        var res = await _client.PostAsJsonAsync("/api/content-types", new
        {
            name, displayName = name,
            fields = new object[]
            {
                new { name = "Title", type = "string", isRequired = true },
                new { name = "Grade", type = "int" },
            },
        }, Ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));
        return name;
    }

    /// <summary>Entries and creation events stored under a type name.</summary>
    private async Task<(int Entries, int Events)> StoredAsync(string type)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var entries = await session.Query<Content>().CountAsync(c => c.ContentType == type, Ct);
        var events = (await session.Events.QueryAllRawEvents().ToListAsync(Ct))
            .Count(e => e.Data is ContentCreated created && created.ContentType == type);
        return (entries, events);
    }

    private async Task<HttpClient> UserAsync()
    {
        var user = _fixture.CreateClient();
        user.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", await _fixture.StoredUserTokenAsync("User"));
        return user;
    }

    private async Task<Guid> CreateEntryAsync(string type)
    {
        var res = await _client.PostAsJsonAsync("/api/contents", new
        {
            contentType = type, data = new Dictionary<string, object> { ["Title"] = "Ana", ["Grade"] = 3 },
        }, Ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));
        return (await res.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
    }

    // The body without its traceId, which differs on every request, so a dry run and a real call compare equal.
    private static async Task<(HttpStatusCode Status, string Body)> ReadAsync(HttpResponseMessage res, CancellationToken ct)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(await res.Content.ReadAsStringAsync(ct));
        if (node is System.Text.Json.Nodes.JsonObject obj)
            obj.Remove("traceId");
        return (res.StatusCode, node?.ToJsonString() ?? "");
    }

    [Fact]
    public async Task A_dry_run_create_leaves_no_entry_and_no_event()
    {
        var type = await TypeAsync();
        var before = await StoredAsync(type);

        var res = await _client.PostAsJsonAsync("/api/contents", new
        {
            contentType = type, dryRun = true, data = new Dictionary<string, object> { ["Title"] = "Ana", ["Grade"] = 3 },
        }, Ct);

        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));
        (await res.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("dryRun").GetBoolean().Should().BeTrue();
        (await StoredAsync(type)).Should().Be(before);
    }

    [Fact]
    public async Task A_dry_run_update_with_an_invalid_body_gets_the_error_the_real_update_gets()
    {
        var type = await TypeAsync();
        var id = await CreateEntryAsync(type);
        var body = new { id, data = new Dictionary<string, object> { ["Grade"] = "three" } };
        var dry = new { id, dryRun = true, data = new Dictionary<string, object> { ["Grade"] = "three" } };

        var real = await ReadAsync(await _client.PutAsJsonAsync($"/api/contents/{id}", body, Ct), Ct);
        var dryRun = await ReadAsync(await _client.PutAsJsonAsync($"/api/contents/{id}", dry, Ct), Ct);

        real.Status.Should().Be(HttpStatusCode.BadRequest);
        dryRun.Should().Be(real);

        // The same route with a valid body takes the dry run path: it says so and changes nothing.
        var valid = await _client.PutAsJsonAsync($"/api/contents/{id}",
            new { id, dryRun = true, data = new Dictionary<string, object> { ["Title"] = "Ana", ["Grade"] = 9 } }, Ct);
        valid.StatusCode.Should().Be(HttpStatusCode.OK, await valid.Content.ReadAsStringAsync(Ct));
        var answer = await valid.Content.ReadFromJsonAsync<JsonElement>(Ct);
        answer.GetProperty("dryRun").GetBoolean().Should().BeTrue();
        answer.GetProperty("changes").GetArrayLength().Should().BeGreaterThan(0);
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        (await session.LoadAsync<Content>(id, Ct))!.Data["Grade"].ToString().Should().Be("3");
    }

    [Fact]
    public async Task A_dry_run_create_with_an_invalid_body_gets_the_error_the_real_create_gets()
    {
        var type = await TypeAsync();
        var data = new Dictionary<string, object> { ["Grade"] = "three" };

        var real = await ReadAsync(await _client.PostAsJsonAsync("/api/contents", new { contentType = type, data }, Ct), Ct);
        var dry = await ReadAsync(await _client.PostAsJsonAsync("/api/contents", new { contentType = type, dryRun = true, data }, Ct), Ct);

        real.Status.Should().Be(HttpStatusCode.BadRequest);
        dry.Should().Be(real);

        // A valid body on the same route answers as a dry run and stores nothing.
        var before = await StoredAsync(type);
        var valid = await _client.PostAsJsonAsync("/api/contents", new
        {
            contentType = type, dryRun = true, data = new Dictionary<string, object> { ["Title"] = "Ana", ["Grade"] = 3 },
        }, Ct);
        valid.StatusCode.Should().Be(HttpStatusCode.OK, await valid.Content.ReadAsStringAsync(Ct));
        var answer = await valid.Content.ReadFromJsonAsync<JsonElement>(Ct);
        answer.GetProperty("dryRun").GetBoolean().Should().BeTrue();
        answer.GetProperty("changes").GetArrayLength().Should().BeGreaterThan(0);
        (await StoredAsync(type)).Should().Be(before);
    }

    [Fact]
    public async Task A_dry_run_entry_write_is_refused_to_a_caller_without_permission()
    {
        var type = await TypeAsync();
        var id = await CreateEntryAsync(type);
        var user = await UserAsync();
        var data = new Dictionary<string, object> { ["Title"] = "Bo", ["Grade"] = 1 };
        var before = await StoredAsync(type);

        (await user.PostAsJsonAsync("/api/contents", new { contentType = type, dryRun = true, data }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await user.PutAsJsonAsync($"/api/contents/{id}", new { id, dryRun = true, data }, Ct))
            .StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.NotFound);
        (await user.PostAsJsonAsync("/api/contents/batch", new
        {
            dryRun = true,
            steps = new object[] { new { op = "create", contentType = type, id = Guid.NewGuid(), data } },
        }, Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await StoredAsync(type)).Should().Be(before);
    }

    [Fact]
    public async Task A_dry_run_batch_answers_for_each_step_and_stores_nothing()
    {
        var type = await TypeAsync();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var before = await StoredAsync(type);

        var res = await _client.PostAsJsonAsync("/api/contents/batch", new
        {
            dryRun = true,
            steps = new object[]
            {
                new { op = "create", contentType = type, id = first, data = new Dictionary<string, object> { ["Title"] = "Ana", ["Grade"] = 3 } },
                new { op = "create", contentType = type, id = second, data = new Dictionary<string, object> { ["Title"] = "Bo" } },
            },
        }, Ct);

        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));
        var body = await res.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("dryRun").GetBoolean().Should().BeTrue();
        var steps = body.GetProperty("steps").EnumerateArray().ToList();
        steps.Should().HaveCount(2);
        steps[0].GetProperty("changes").EnumerateArray().Select(c => c.GetProperty("field").GetString())
            .Should().BeEquivalentTo(["Title", "Grade"]);

        (await StoredAsync(type)).Should().Be(before);
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        (await session.LoadAsync<Content>(first, Ct)).Should().BeNull();
        (await session.Events.FetchStreamStateAsync(second, Ct)).Should().BeNull();

        var real = await _client.PostAsJsonAsync("/api/contents/batch", new
        {
            steps = new object[] { new { op = "create", contentType = type, id = first, data = new Dictionary<string, object> { ["Title"] = "Ana" } } },
        }, Ct);
        real.StatusCode.Should().Be(HttpStatusCode.OK, "the dry run left the id free");
    }

    [Fact]
    public async Task A_dry_run_batch_with_an_invalid_step_gets_the_error_the_real_batch_gets()
    {
        var type = await TypeAsync();
        object Steps() => new object[]
        {
            new { op = "create", contentType = type, id = Guid.NewGuid(), data = new Dictionary<string, object> { ["Grade"] = "three" } },
        };

        var real = await _client.PostAsJsonAsync("/api/contents/batch", new { steps = Steps() }, Ct);
        var dry = await _client.PostAsJsonAsync("/api/contents/batch", new { dryRun = true, steps = Steps() }, Ct);

        real.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        dry.StatusCode.Should().Be(real.StatusCode);
        var realErrors = await real.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var dryErrors = await dry.Content.ReadFromJsonAsync<JsonElement>(Ct);
        dryErrors.GetProperty("errors").GetRawText().Should().Be(realErrors.GetProperty("errors").GetRawText());
        (await StoredAsync(type)).Entries.Should().Be(0);
    }

    private async Task<ContentTypeDefinition> DefinitionAsync(string type)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return await session.Query<ContentTypeDefinition>().SingleAsync(d => d.Name == type, Ct);
    }

    private async Task<int> FieldChangeRowsAsync(string type)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return await session.Query<FieldChange>().CountAsync(c => c.ContentType == type, Ct);
    }

    private async Task<int> AuditRowsAsync(string type)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return await session.Query<AuditEvent>()
            .CountAsync(e => e.Action == "contenttype.field_changed" && e.TargetId == type, Ct);
    }

    [Fact]
    public async Task A_dry_run_rename_names_the_change_and_starts_no_job()
    {
        var type = await TypeAsync();
        await CreateEntryAsync(type);

        var res = await _client.PostAsJsonAsync($"/api/content-types/{type}/fields/Grade/change", new { newName = "Score", dryRun = true }, Ct);

        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));
        var body = await res.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("dryRun").GetBoolean().Should().BeTrue();
        body.GetProperty("classification").GetString().Should().Be("rename");
        body.GetProperty("change").ValueKind.Should().Be(JsonValueKind.Null);
        (await DefinitionAsync(type)).Fields.Select(f => f.Name).Should().Equal("Title", "Grade");

        (await FieldChangeRowsAsync(type)).Should().Be(0);
    }

    [Fact]
    public async Task A_dry_run_remove_is_refused_without_allowDestructive_and_counts_the_values_with_it()
    {
        var type = await TypeAsync();
        await CreateEntryAsync(type);
        var path = $"/api/content-types/{type}/fields/Grade/change";

        var refused = await _client.PostAsJsonAsync(path, new { remove = true, dryRun = true }, Ct);
        var real = await _client.PostAsJsonAsync(path, new { remove = true }, Ct);
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadAsync(refused, Ct)).Should().Be(await ReadAsync(real, Ct));

        var allowed = await _client.PostAsJsonAsync(path, new { remove = true, allowDestructive = true, dryRun = true }, Ct);
        allowed.StatusCode.Should().Be(HttpStatusCode.OK, await allowed.Content.ReadAsStringAsync(Ct));
        var body = await allowed.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("dryRun").GetBoolean().Should().BeTrue();
        body.GetProperty("classification").GetString().Should().Be("destructive");
        body.GetProperty("valuesLost").GetInt32().Should().Be(1);
        body.GetProperty("change").ValueKind.Should().Be(JsonValueKind.Null, "no job was started");
        (await DefinitionAsync(type)).Fields.Select(f => f.Name).Should().Equal("Title", "Grade");
        (await FieldChangeRowsAsync(type)).Should().Be(0);
        (await AuditRowsAsync(type)).Should().Be(0);
    }

    [Fact]
    public async Task A_dry_run_label_change_leaves_the_label()
    {
        var type = await TypeAsync();
        var before = (await DefinitionAsync(type)).Fields.Single(f => f.Name == "Title").DisplayName;

        var res = await _client.PostAsJsonAsync($"/api/content-types/{type}/fields/Title/change", new { displayName = "Name", dryRun = true }, Ct);

        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));
        var body = await res.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("dryRun").GetBoolean().Should().BeTrue();
        body.GetProperty("classification").GetString().Should().Be("additive");
        (await DefinitionAsync(type)).Fields.Single(f => f.Name == "Title").DisplayName.Should().Be(before);
        (await AuditRowsAsync(type)).Should().Be(0);
    }

    [Fact]
    public async Task A_dry_run_field_change_needs_the_permission_the_change_needs()
    {
        var type = await TypeAsync();
        var user = await UserAsync();

        (await user.PostAsJsonAsync($"/api/content-types/{type}/fields/Grade/change", new { newName = "Score", dryRun = true }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_dry_run_field_change_leaves_no_audit_row_and_no_job_row_where_the_real_one_leaves_both()
    {
        var type = await TypeAsync();
        await CreateEntryAsync(type);
        var path = $"/api/content-types/{type}/fields/Grade/change";

        foreach (var body in new object[]
        {
            new { newName = "Score", dryRun = true },
            new { toType = "string", dryRun = true },
            new { remove = true, allowDestructive = true, dryRun = true },
            new { displayName = "Mark", dryRun = true },
        })
        {
            var res = await _client.PostAsJsonAsync(path, body, Ct);
            res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));
            (await res.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("dryRun").GetBoolean().Should().BeTrue();
        }

        (await AuditRowsAsync(type)).Should().Be(0);
        (await FieldChangeRowsAsync(type)).Should().Be(0);

        // The real change of the same field writes both, so the zeros above are the dry run's doing.
        (await _client.PostAsJsonAsync(path, new { displayName = "Mark" }, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await AuditRowsAsync(type)).Should().Be(1);
    }

    [Fact]
    public async Task A_dry_run_field_change_while_another_runs_gets_the_409_the_real_one_gets()
    {
        var type = await TypeAsync();
        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new FieldChange
            {
                Id = Guid.NewGuid(), ContentType = type, Field = "Title", Kind = FieldChangeKind.Rename,
                NewName = "Heading", State = FieldChangeState.Running, Total = 5,
                StartedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            await session.SaveChangesAsync(Ct);
        }

        var path = $"/api/content-types/{type}/fields/Grade/change";
        foreach (var change in new object[] { new { newName = "Score" }, new { toType = "string" } })
        {
            var real = await ReadAsync(await _client.PostAsJsonAsync(path, change, Ct), Ct);
            var dry = await ReadAsync(await _client.PostAsJsonAsync(path, DryRun(change), Ct), Ct);

            real.Status.Should().Be(HttpStatusCode.Conflict);
            dry.Should().Be(real);
        }

        (await FieldChangeRowsAsync(type)).Should().Be(1, "only the change that was already running");
    }

    private static Dictionary<string, object> DryRun(object change)
    {
        var map = JsonSerializer.SerializeToElement(change).EnumerateObject()
            .ToDictionary(p => p.Name, p => (object)p.Value.Clone());
        map["dryRun"] = true;
        return map;
    }

    [Fact]
    public async Task A_dry_run_batch_with_a_bad_second_step_fails_at_that_step_as_the_real_batch_does()
    {
        var type = await TypeAsync();
        object Steps() => new object[]
        {
            new { op = "create", contentType = type, id = Guid.NewGuid(), data = new Dictionary<string, object> { ["Title"] = "Ana" } },
            new { op = "create", contentType = type, id = Guid.NewGuid(), data = new Dictionary<string, object> { ["Grade"] = "three" } },
        };

        var real = await _client.PostAsJsonAsync("/api/contents/batch", new { steps = Steps() }, Ct);
        var dry = await _client.PostAsJsonAsync("/api/contents/batch", new { dryRun = true, steps = Steps() }, Ct);

        real.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadAsync(dry, Ct)).Should().Be(await ReadAsync(real, Ct));
        (await real.Content.ReadAsStringAsync(Ct)).Should().Contain("steps[1]");
        (await StoredAsync(type)).Entries.Should().Be(0, "the real batch rolled back too, so neither left the good first step");
    }

    private async Task<string> NumberedTypeAsync(string strategy)
    {
        var name = NewName();
        (await _client.PostAsJsonAsync("/api/content-types", new
        {
            name, displayName = name,
            fields = new object[] { new { name = "Title", type = "string" }, new { name = "Number", type = "string" } },
        }, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        var set = await _client.PutAsJsonAsync($"/api/content-types/{name}/numbering",
            new { field = "number", prefix = "INV-", padding = 5, strategy }, Ct);
        set.StatusCode.Should().Be(HttpStatusCode.OK, await set.Content.ReadAsStringAsync(Ct));
        return name;
    }

    private async Task<long> CounterAsync(string type)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "select coalesce(max(last_value), 0) from public.barako_number_counters where tenant_id = '*DEFAULT*' and series = @series", connection);
        command.Parameters.AddWithValue("series", "type." + type.ToLowerInvariant());
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    [Theory]
    [InlineData("gapless")]
    [InlineData("sequence")]
    [InlineData("hiLo")]
    public async Task A_dry_run_batch_takes_no_number_and_shows_none(string strategy)
    {
        var type = await NumberedTypeAsync(strategy);
        (await _client.PostAsJsonAsync("/api/contents", new { contentType = type, data = new { Title = "first" } }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var before = await CounterAsync(type);
        before.Should().BeGreaterThan(0, "the real create took a number, so the counter row exists");

        var res = await _client.PostAsJsonAsync("/api/contents/batch", new
        {
            dryRun = true,
            steps = new object[]
            {
                new { op = "create", contentType = type, id = Guid.NewGuid(), data = new Dictionary<string, object> { ["Title"] = "a" } },
                new { op = "create", contentType = type, id = Guid.NewGuid(), data = new Dictionary<string, object> { ["Title"] = "b" } },
            },
        }, Ct);

        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));
        var body = await res.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("dryRun").GetBoolean().Should().BeTrue();
        var steps = body.GetProperty("steps").EnumerateArray().ToList();
        steps.Should().HaveCount(2);
        steps.Should().OnlyContain(s => s.GetProperty("changes").EnumerateArray()
            .All(c => !string.Equals(c.GetProperty("field").GetString(), "Number", StringComparison.OrdinalIgnoreCase)));
        (await CounterAsync(type)).Should().Be(before, "a dry run does not move the series' counter");
    }
}
