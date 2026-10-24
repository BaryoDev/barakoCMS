using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Models;
using Xunit;

namespace BarakoCMS.Tests.Features.ContentTypes;

/// <summary>
/// Content type and entry writes take <c>dryRun</c>: the write is checked by everything the real
/// one runs, and the answer says what it would change, with nothing stored (#892).
/// </summary>
[Collection("Integration1")]
public class DryRunTests : IAsyncLifetime
{
    private readonly IntegrationTestFixture _fixture;
    private readonly HttpClient _client;

    public DryRunTests(IntegrationTestFixture fixture)
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

    private static string NewName() => "dryrun" + Guid.NewGuid().ToString("n")[..10];

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

    private async Task<(int Entries, int Types)> StoredAsync(string type)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return (await session.Query<Content>().CountAsync(c => c.ContentType == type, Ct),
                await session.Query<ContentTypeDefinition>().CountAsync(d => d.Name == type, Ct));
    }

    private static List<(string Field, JsonElement From, JsonElement To)> Changes(JsonElement body) =>
        body.GetProperty("changes").EnumerateArray()
            .Select(c => (c.GetProperty("field").GetString()!, c.GetProperty("from"), c.GetProperty("to")))
            .ToList();

    [Fact]
    public async Task A_dry_run_create_answers_with_the_fields_it_would_set_and_stores_no_entry()
    {
        var type = await TypeAsync();

        var res = await _client.PostAsJsonAsync("/api/contents", new
        {
            contentType = type, dryRun = true, data = new Dictionary<string, object> { ["Title"] = "Ana", ["Grade"] = 3 },
        }, Ct);

        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));
        var body = await res.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("dryRun").GetBoolean().Should().BeTrue();
        var changes = Changes(body);
        changes.Should().HaveCount(2);
        changes.Select(c => c.Field).Should().BeEquivalentTo(["Title", "Grade"]);
        changes.Should().OnlyContain(c => c.From.ValueKind == JsonValueKind.Null);
        (await StoredAsync(type)).Entries.Should().Be(0);
    }

    [Fact]
    public async Task A_dry_run_create_is_refused_as_the_real_one_would_be()
    {
        var type = await TypeAsync();

        var res = await _client.PostAsJsonAsync("/api/contents", new
        {
            contentType = type, dryRun = true, data = new Dictionary<string, object> { ["Grade"] = "three" },
        }, Ct);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadAsStringAsync(Ct);
        body.Should().Contain("(Title) is required").And.Contain("expects type 'int'");
        (await StoredAsync(type)).Entries.Should().Be(0);
    }

    [Fact]
    public async Task A_dry_run_update_answers_with_the_diff_and_leaves_the_entry_as_it_was()
    {
        var type = await TypeAsync();
        var created = await _client.PostAsJsonAsync("/api/contents", new
        {
            contentType = type, data = new Dictionary<string, object> { ["Title"] = "Ana", ["Grade"] = 3 },
        }, Ct);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();

        var res = await _client.PutAsJsonAsync($"/api/contents/{id}", new
        {
            id, dryRun = true, data = new Dictionary<string, object> { ["Title"] = "Ana", ["Grade"] = 4 },
        }, Ct);

        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));
        var changes = Changes(await res.Content.ReadFromJsonAsync<JsonElement>(Ct));
        changes.Should().ContainSingle();
        changes[0].Field.Should().Be("Grade");
        changes[0].From.GetInt64().Should().Be(3);
        changes[0].To.GetInt64().Should().Be(4);

        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var stored = await session.LoadAsync<Content>(id, Ct);
        Convert.ToInt64(stored!.Data["Grade"]).Should().Be(3);
        (await session.Events.FetchStreamStateAsync(id, Ct))!.Version.Should().Be(1, "no event was appended");
    }

    /// <summary>A client for a stored user whose one role grants Update on the type and nothing else.</summary>
    private async Task<HttpClient> UpdateOnlyAsync(string type)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var role = new Role
        {
            Id = Guid.NewGuid(), Name = $"UpdateOnly_{Guid.NewGuid():n}",
            Permissions = [new ContentTypePermission { ContentTypeSlug = type, Update = new PermissionRule { Enabled = true } }],
        };
        var userId = Guid.NewGuid();
        session.Store(role);
        session.Store(new User { Id = userId, Username = $"updateonly-{userId:n}", Email = $"updateonly-{userId:n}@example.com", RoleIds = [role.Id] });
        await session.SaveChangesAsync(Ct);

        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", _fixture.CreateToken([role.Name], userId.ToString(), new Dictionary<string, string> { ["Username"] = $"updateonly-{userId:n}" }));
        return client;
    }

    [Fact]
    public async Task A_dry_run_update_by_a_caller_who_may_not_read_the_entry_tells_them_nothing_stored()
    {
        var type = await TypeAsync();
        var created = await _client.PostAsJsonAsync("/api/contents", new
        {
            contentType = type, data = new Dictionary<string, object> { ["Title"] = "Ana", ["Grade"] = 85000 },
        }, Ct);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        var writer = await UpdateOnlyAsync(type);
        (await writer.GetAsync($"/api/contents/{id}", Ct)).IsSuccessStatusCode.Should().BeFalse("the role does not read the entry");

        var res = await writer.PutAsJsonAsync($"/api/contents/{id}", new
        {
            id, dryRun = true, data = new Dictionary<string, object> { ["Title"] = "Ana", ["Grade"] = 1 },
        }, Ct);

        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));
        (await res.Content.ReadAsStringAsync(Ct)).Should().NotContain("85000");
        var changes = Changes(await res.Content.ReadFromJsonAsync<JsonElement>(Ct));
        changes.Select(c => c.Field).Should().BeEquivalentTo(["Title", "Grade"], "every field sent, since which ones differ would say what is stored");
        changes.Should().OnlyContain(c => c.From.ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task A_dry_run_leaves_its_idempotency_key_for_the_real_write()
    {
        var type = await TypeAsync();
        var key = Guid.NewGuid().ToString("n");

        async Task<HttpResponseMessage> CreateAsync(bool dryRun)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/contents")
            {
                Content = JsonContent.Create(new { contentType = type, dryRun, data = new Dictionary<string, object> { ["Title"] = "Ana" } }),
            };
            request.Headers.Add("Idempotency-Key", key);
            return await _client.SendAsync(request, Ct);
        }

        (await CreateAsync(dryRun: true)).StatusCode.Should().Be(HttpStatusCode.OK);
        var real = await CreateAsync(dryRun: false);

        real.StatusCode.Should().Be(HttpStatusCode.OK, await real.Content.ReadAsStringAsync(Ct));
        (await StoredAsync(type)).Entries.Should().Be(1);
    }

    [Fact]
    public async Task A_dry_run_type_create_lists_its_fields_and_stores_no_type()
    {
        var name = NewName();

        var res = await _client.PostAsJsonAsync("/api/content-types", new
        {
            name, displayName = name, dryRun = true,
            fields = new object[] { new { name = "Title", type = "string" }, new { name = "Price", type = "decimal" } },
        }, Ct);

        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));
        var changes = Changes(await res.Content.ReadFromJsonAsync<JsonElement>(Ct));
        changes.Select(c => (c.Field, c.To.GetString())).Should().Equal(("Title", "string"), ("Price", "decimal"));
        (await StoredAsync(name)).Types.Should().Be(0);

        var real = await _client.PostAsJsonAsync("/api/content-types", new
        {
            name, displayName = name, fields = new object[] { new { name = "Title", type = "string" } },
        }, Ct);
        real.StatusCode.Should().Be(HttpStatusCode.OK, "the dry run left the name free");
    }

    [Fact]
    public async Task A_dry_run_type_create_is_refused_as_the_real_one_would_be()
    {
        var res = await _client.PostAsJsonAsync("/api/content-types", new
        {
            name = NewName(), displayName = "x", dryRun = true, fields = new object[] { new { name = "title", type = "string" } },
        }, Ct);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_dry_run_add_field_names_the_field_and_leaves_the_type_as_it_was()
    {
        var type = await TypeAsync();

        var res = await _client.PostAsJsonAsync($"/api/content-types/{type}/fields", new
        {
            fieldName = "Notes", type = "text", dryRun = true,
        }, Ct);

        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));
        var body = await res.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("fields").EnumerateArray().Select(f => f.GetString()).Should().Equal("Title", "Grade", "Notes");
        Changes(body).Should().ContainSingle().Which.Field.Should().Be("Notes");

        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        (await session.Query<ContentTypeDefinition>().SingleAsync(d => d.Name == type, Ct))
            .Fields.Select(f => f.Name).Should().Equal("Title", "Grade");
    }

    [Fact]
    public async Task A_dry_run_still_needs_the_permission_the_write_needs()
    {
        var type = await TypeAsync();
        var user = _fixture.CreateClient();
        user.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", await _fixture.StoredUserTokenAsync("User"));

        (await user.PostAsJsonAsync("/api/content-types", new
        {
            name = NewName(), displayName = "x", dryRun = true, fields = new object[] { new { name = "Title", type = "string" } },
        }, Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await user.PostAsJsonAsync($"/api/content-types/{type}/fields", new { fieldName = "Notes", type = "text", dryRun = true }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
