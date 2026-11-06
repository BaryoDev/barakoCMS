using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using barakoCMS.Events;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
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

    private static async Task<(HttpStatusCode Status, string Body)> ReadAsync(HttpResponseMessage res, CancellationToken ct) =>
        (res.StatusCode, await res.Content.ReadAsStringAsync(ct));

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
    }

    [Fact]
    public async Task A_dry_run_entry_write_is_refused_to_a_caller_without_permission()
    {
        var type = await TypeAsync();
        var id = await CreateEntryAsync(type);
        var user = await UserAsync();
        var data = new Dictionary<string, object> { ["Title"] = "Bo", ["Grade"] = 1 };

        (await user.PostAsJsonAsync("/api/contents", new { contentType = type, dryRun = true, data }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await user.PutAsJsonAsync($"/api/contents/{id}", new { id, dryRun = true, data }, Ct))
            .StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.NotFound);
        (await user.PostAsJsonAsync("/api/contents/batch", new
        {
            dryRun = true,
            steps = new object[] { new { op = "create", contentType = type, id = Guid.NewGuid(), data } },
        }, Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
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

        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        (await session.Query<FieldChange>().CountAsync(c => c.ContentType == type, Ct)).Should().Be(0);
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
        (await refused.Content.ReadAsStringAsync(Ct)).Should().Be(await real.Content.ReadAsStringAsync(Ct));

        var allowed = await _client.PostAsJsonAsync(path, new { remove = true, allowDestructive = true, dryRun = true }, Ct);
        allowed.StatusCode.Should().Be(HttpStatusCode.OK, await allowed.Content.ReadAsStringAsync(Ct));
        var body = await allowed.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("classification").GetString().Should().Be("destructive");
        body.GetProperty("valuesLost").GetInt32().Should().Be(1);
        (await DefinitionAsync(type)).Fields.Select(f => f.Name).Should().Equal("Title", "Grade");
    }

    [Fact]
    public async Task A_dry_run_label_change_leaves_the_label()
    {
        var type = await TypeAsync();
        var before = (await DefinitionAsync(type)).Fields.Single(f => f.Name == "Title").DisplayName;

        var res = await _client.PostAsJsonAsync($"/api/content-types/{type}/fields/Title/change", new { displayName = "Name", dryRun = true }, Ct);

        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));
        (await DefinitionAsync(type)).Fields.Single(f => f.Name == "Title").DisplayName.Should().Be(before);
    }

    [Fact]
    public async Task A_dry_run_field_change_needs_the_permission_the_change_needs()
    {
        var type = await TypeAsync();
        var user = await UserAsync();

        (await user.PostAsJsonAsync($"/api/content-types/{type}/fields/Grade/change", new { newName = "Score", dryRun = true }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
