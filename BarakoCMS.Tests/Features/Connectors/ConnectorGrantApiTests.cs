using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Features.Connectors;

/// <summary>
/// What the connector endpoints do with <c>allowedFields</c>: who may grant, what a save keeps or
/// clears, who sees the list and what the audit trail holds (#1423).
/// </summary>
[Collection("Integration1")]
public class ConnectorGrantApiTests(IntegrationTestFixture fixture)
{
    private const string Home = "https://crm.example.com/v1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] ManagerCaps = [SystemCapabilities.ManageConnectors, SystemCapabilities.ViewConnectors];

    private static readonly string[] ReaderCaps = [.. ManagerCaps, SystemCapabilities.ViewSensitive];

    private async Task<string> TypeAsync() => (await GrantKit.SeedAsync(fixture, [])).Type;

    /// <summary>A manager who may read the type's fields, with the id the audit trail names.</summary>
    private Task<(HttpClient Client, Guid UserId)> ReaderAsync(string type) => GrantKit.CallerOfAsync(fixture, ReaderCaps, type);

    /// <summary>A manager who holds no right to read a Sensitive field.</summary>
    private Task<HttpClient> NonReaderAsync() => GrantKit.CallerAsync(fixture, ManagerCaps);

    private static JsonObject Payload(string slug, string baseUrl, string[]? allowed)
    {
        var body = new JsonObject
        {
            ["name"] = "Link", ["slug"] = slug, ["baseUrl"] = baseUrl, ["auth"] = "None",
            ["settings"] = new JsonObject(), ["enabled"] = true, ["probePath"] = "/",
        };
        if (allowed is not null) body["allowedFields"] = new JsonArray([.. allowed.Select(a => (JsonNode?)JsonValue.Create(a))]);
        return body;
    }

    private static Task<HttpResponseMessage> CreateAsync(HttpClient client, string slug, string baseUrl, string[]? allowed) =>
        client.PostAsJsonAsync("/api/connectors", Payload(slug, baseUrl, allowed), Ct);

    private static Task<HttpResponseMessage> SaveAsync(HttpClient client, string slug, string baseUrl, string[]? allowed) =>
        client.PutAsJsonAsync($"/api/connectors/{slug}", Payload(slug, baseUrl, allowed), Ct);

    private async Task<string> CreatedAsync(HttpClient client, string baseUrl, string[]? allowed)
    {
        var slug = GrantKit.Name("lk");
        var response = await CreateAsync(client, slug, baseUrl, allowed);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return slug;
    }

    private async Task<Connector> StoredAsync(string slug)
    {
        using var scope = fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IQuerySession>()
            .Query<Connector>().SingleAsync(c => c.Slug == slug, Ct);
    }

    private async Task<List<AuditEvent>> AuditAsync(string slug)
    {
        using var scope = fixture.Services.CreateScope();
        var rows = await scope.ServiceProvider.GetRequiredService<IQuerySession>().Query<AuditEvent>()
            .Where(e => e.Action.StartsWith("connector.")).ToListAsync(Ct);
        return rows.Where(e => e.Metadata != null && e.Metadata["slug"].ToString() == slug).OrderBy(e => e.CreatedAt).ToList();
    }

    private static string[]? GrantsIn(JsonElement connector) =>
        connector.TryGetProperty("allowedFields", out var list) && list.ValueKind == JsonValueKind.Array
            ? [.. list.EnumerateArray().Select(e => e.GetString()!)]
            : null;

    [Fact]
    public async Task A_reader_creates_a_connector_with_a_grant_and_the_audit_row_holds_the_list_and_the_user()
    {
        var type = await TypeAsync();
        var (reader, userId) = await ReaderAsync(type);

        var slug = await CreatedAsync(reader, Home, [$"{type}.Ssn"]);

        (await StoredAsync(slug)).AllowedFields.Should().Equal($"{type}.Ssn");
        var row = (await AuditAsync(slug)).Should().ContainSingle(e => e.Action == "connector.created").Subject;
        row.ActorUserId.Should().Be(userId);
        row.Metadata!["allowedFieldsBefore"].ToString().Should().BeEmpty();
        row.Metadata["allowedFieldsAfter"].ToString().Should().Be($"{type}.Ssn");
        JsonSerializer.Serialize(row.Metadata).Should().NotContain(GrantKit.SsnValue, "an audit row names a field and never its value");
    }

    [Fact]
    public async Task An_update_that_changes_the_grant_audits_the_list_before_and_after()
    {
        var type = await TypeAsync();
        var (reader, userId) = await ReaderAsync(type);
        var slug = await CreatedAsync(reader, Home, [$"{type}.Ssn"]);

        var response = await SaveAsync(reader, slug, Home, [$"{type}.Perk"]);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var row = (await AuditAsync(slug)).Should().ContainSingle(e => e.Action == "connector.updated").Subject;
        row.ActorUserId.Should().Be(userId);
        row.Metadata!["allowedFieldsBefore"].ToString().Should().Be($"{type}.Ssn");
        row.Metadata["allowedFieldsAfter"].ToString().Should().Be($"{type}.Perk");
    }

    [Fact]
    public async Task A_delete_audits_the_list_the_connector_held()
    {
        var type = await TypeAsync();
        var (reader, _) = await ReaderAsync(type);
        var slug = await CreatedAsync(reader, Home, [$"{type}.Ssn", $"{type}.Perk"]);

        var response = await reader.DeleteAsync($"/api/connectors/{slug}", Ct);

        response.IsSuccessStatusCode.Should().BeTrue(await response.Content.ReadAsStringAsync(Ct));
        var row = (await AuditAsync(slug)).Should().ContainSingle(e => e.Action == "connector.deleted").Subject;
        row.Metadata!["allowedFieldsBefore"].ToString().Should().Be($"{type}.Ssn, {type}.Perk");
        row.Metadata["allowedFieldsAfter"].ToString().Should().BeEmpty();
    }

    [Fact]
    public async Task A_connector_without_a_grant_audits_no_grant_lines()
    {
        var type = await TypeAsync();
        var (reader, _) = await ReaderAsync(type);

        var slug = await CreatedAsync(reader, Home, null);

        var row = (await AuditAsync(slug)).Should().ContainSingle().Subject;
        row.Metadata!.Keys.Should().NotContain(["allowedFieldsBefore", "allowedFieldsAfter"]);
    }

    [Fact]
    public async Task A_manager_who_may_not_read_a_field_cannot_grant_it_and_nothing_is_stored()
    {
        var type = await TypeAsync();
        var slug = GrantKit.Name("lk");

        var response = await CreateAsync(await NonReaderAsync(), slug, Home, [$"{type}.Ssn"]);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain($"{type}.Ssn");
        using var scope = fixture.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<IQuerySession>().Query<Connector>().AnyAsync(c => c.Slug == slug, Ct))
            .Should().BeFalse();
    }

    [Theory]
    [InlineData("{type}.Vault", "Hidden")]
    [InlineData("{type}.Memo", "hidden by its owner")]
    [InlineData("nosuchtype.Ssn", "does not exist")]
    [InlineData("{type}.Missing", "does not have")]
    [InlineData("Ssn", "not a grant")]
    [InlineData("{type}.Ssn.Part", "not a grant")]
    [InlineData("{type}.", "not a grant")]
    public async Task An_entry_that_cannot_be_granted_is_refused_with_the_reason(string entry, string reason)
    {
        var type = await TypeAsync();
        var (reader, _) = await ReaderAsync(type);
        var slug = GrantKit.Name("lk");

        var response = await CreateAsync(reader, slug, Home, [entry.Replace("{type}", type)]);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync(Ct));
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain(reason);
    }

    [Fact]
    public async Task A_list_over_the_limit_is_refused()
    {
        var type = await TypeAsync();
        var (reader, _) = await ReaderAsync(type);
        var entries = Enumerable.Range(0, 201).Select(i => $"{type}.F{i}").ToArray();

        var response = await CreateAsync(reader, GrantKit.Name("lk"), Home, entries);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("https://crm.example.com/v2", true)]
    [InlineData("https://crm.example.com", true)]
    [InlineData("https://other.example.com/v1", true)]
    [InlineData("https://crm.example.com/V1", true)]
    [InlineData("https://crm.example.com/v1/", false)]
    [InlineData("https://CRM.example.com/v1", false)]
    public async Task A_new_base_url_clears_the_grant_unless_only_case_of_the_host_or_a_trailing_slash_changed(string next, bool clears)
    {
        var type = await TypeAsync();
        var (reader, _) = await ReaderAsync(type);
        var slug = await CreatedAsync(reader, Home, [$"{type}.Ssn"]);

        var response = await SaveAsync(reader, slug, next, null);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var stored = await StoredAsync(slug);
        if (clears) stored.AllowedFields.Should().BeNull();
        else stored.AllowedFields.Should().Equal($"{type}.Ssn");
    }

    [Fact]
    public async Task A_move_clears_the_grant_and_the_audit_row_shows_it_going()
    {
        var type = await TypeAsync();
        var (reader, _) = await ReaderAsync(type);
        var slug = await CreatedAsync(reader, Home, [$"{type}.Ssn"]);

        await SaveAsync(reader, slug, "https://crm.example.com/v2", null);

        var row = (await AuditAsync(slug)).Last();
        row.Metadata!["allowedFieldsBefore"].ToString().Should().Be($"{type}.Ssn");
        row.Metadata["allowedFieldsAfter"].ToString().Should().BeEmpty();
    }

    [Fact]
    public async Task A_list_sent_with_a_move_is_judged_as_all_new_so_the_caller_needs_read_on_each_entry()
    {
        var type = await TypeAsync();
        var (reader, _) = await ReaderAsync(type);
        var slug = await CreatedAsync(reader, Home, [$"{type}.Ssn"]);

        var refused = await SaveAsync(await NonReaderAsync(), slug, "https://crm.example.com/v2", [$"{type}.Ssn"]);

        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden, "the stored entry was cleared by the move, so this one is new");
        var untouched = await StoredAsync(slug);
        untouched.BaseUrl.Should().Be(Home, "the move and the clear are never applied apart");
        untouched.AllowedFields.Should().Equal($"{type}.Ssn");

        var allowed = await SaveAsync(reader, slug, "https://crm.example.com/v2", [$"{type}.Ssn"]);

        allowed.StatusCode.Should().Be(HttpStatusCode.OK, await allowed.Content.ReadAsStringAsync(Ct));
        var moved = await StoredAsync(slug);
        moved.BaseUrl.Should().Be("https://crm.example.com/v2");
        moved.AllowedFields.Should().Equal($"{type}.Ssn");
    }

    [Fact]
    public async Task A_save_with_no_list_keeps_the_stored_grant()
    {
        var type = await TypeAsync();
        var (reader, _) = await ReaderAsync(type);
        var slug = await CreatedAsync(reader, Home, [$"{type}.Ssn"]);

        await SaveAsync(reader, slug, Home, null);

        (await StoredAsync(slug)).AllowedFields.Should().Equal($"{type}.Ssn");
    }

    [Fact]
    public async Task A_save_with_an_empty_list_clears_the_grant()
    {
        var type = await TypeAsync();
        var (reader, _) = await ReaderAsync(type);
        var slug = await CreatedAsync(reader, Home, [$"{type}.Ssn"]);

        await SaveAsync(reader, slug, Home, []);

        (await StoredAsync(slug)).AllowedFields.Should().BeNull();
    }

    [Fact]
    public async Task A_manager_without_read_rights_resending_the_stored_list_keeps_it()
    {
        var type = await TypeAsync();
        var (reader, _) = await ReaderAsync(type);
        var slug = await CreatedAsync(reader, Home, [$"{type}.Perk"]);

        var response = await SaveAsync(await NonReaderAsync(), slug, Home, [$"{type}.Perk"]);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        (await StoredAsync(slug)).AllowedFields.Should().Equal($"{type}.Perk");
    }

    [Fact]
    public async Task A_manager_without_read_rights_cannot_add_an_entry_but_can_remove_one()
    {
        var type = await TypeAsync();
        var (reader, _) = await ReaderAsync(type);
        var slug = await CreatedAsync(reader, Home, [$"{type}.Perk", $"{type}.Ssn"]);
        var manager = await NonReaderAsync();

        var other = await CreatedAsync(reader, Home, [$"{type}.Perk"]);
        var added = await SaveAsync(manager, other, Home, [$"{type}.Perk", $"{type}.Ssn"]);
        var removed = await SaveAsync(manager, slug, Home, [$"{type}.Perk"]);

        added.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await StoredAsync(other)).AllowedFields.Should().Equal($"{type}.Perk");
        removed.StatusCode.Should().Be(HttpStatusCode.OK, await removed.Content.ReadAsStringAsync(Ct));
        (await StoredAsync(slug)).AllowedFields.Should().Equal($"{type}.Perk");
    }

    [Fact]
    public async Task A_case_variant_of_a_stored_entry_is_kept_as_one_entry()
    {
        var type = await TypeAsync();
        var (reader, _) = await ReaderAsync(type);

        var slug = await CreatedAsync(reader, Home, [$"{type}.Ssn", $"{type.ToUpperInvariant()}.SSN"]);

        (await StoredAsync(slug)).AllowedFields.Should().Equal($"{type}.Ssn");
    }

    [Fact]
    public async Task A_manager_sees_the_grant_in_the_list_and_in_one_connector()
    {
        var type = await TypeAsync();
        var (reader, _) = await ReaderAsync(type);
        var slug = await CreatedAsync(reader, Home, [$"{type}.Ssn"]);

        var one = await (await reader.GetAsync($"/api/connectors/{slug}", Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        var list = await (await reader.GetAsync("/api/connectors?pageSize=100", Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);

        GrantsIn(one).Should().Equal($"{type}.Ssn");
        GrantsIn(list.GetProperty("items").EnumerateArray().Single(c => c.GetProperty("slug").GetString() == slug))
            .Should().Equal($"{type}.Ssn");
    }

    [Fact]
    public async Task A_caller_who_may_only_view_connectors_sees_no_grant_in_the_list_or_in_one_connector()
    {
        var type = await TypeAsync();
        var (reader, _) = await ReaderAsync(type);
        var slug = await CreatedAsync(reader, Home, [$"{type}.Ssn"]);
        var viewer = await GrantKit.CallerAsync(fixture, [SystemCapabilities.ViewConnectors]);

        var oneResponse = await viewer.GetAsync($"/api/connectors/{slug}", Ct);
        var listResponse = await viewer.GetAsync("/api/connectors?pageSize=100", Ct);

        oneResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var one = await oneResponse.Content.ReadAsStringAsync(Ct);
        var list = await listResponse.Content.ReadAsStringAsync(Ct);
        one.Should().NotContain($"{type}.Ssn");
        list.Should().NotContain($"{type}.Ssn");
        GrantsIn(JsonDocument.Parse(one).RootElement).Should().BeNull();
        GrantsIn(JsonDocument.Parse(list).RootElement.GetProperty("items").EnumerateArray()
            .Single(c => c.GetProperty("slug").GetString() == slug)).Should().BeNull();
    }

    [Fact]
    public void A_connector_stored_by_5_9_0_loads_with_no_grant()
    {
        var serializer = fixture.Services.GetRequiredService<IDocumentStore>().Options.Serializer();
        var written = JsonNode.Parse(serializer.ToJson(new Connector
        {
            Id = Guid.NewGuid(), Name = "Old", Slug = "old-link", BaseUrl = "https://old.example.com", AllowedFields = ["x.Y"],
        }))!.AsObject();

        // 5.9.0 wrote no such property at all.
        var key = written.Select(p => p.Key).Single(k => k.Equals("allowedFields", StringComparison.OrdinalIgnoreCase));
        written.Remove(key);
        var connector = serializer.FromJson<Connector>(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(written.ToJsonString())));

        connector.Slug.Should().Be("old-link");
        connector.AllowedFields.Should().BeNull("a connector stored before this release has no grant");
    }

    private async Task RunFieldChangeAsync(string type) => await GrantKit.StoreAsync(fixture, new FieldChange
    {
        Id = Guid.NewGuid(), ContentType = type, Field = "Perk", Kind = FieldChangeKind.Rename, NewName = "Bonus",
        State = FieldChangeState.Running, Total = 1, StartedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    });

    [Fact]
    public async Task A_grant_naming_a_type_with_a_field_change_running_is_refused_with_409_on_create_and_on_update()
    {
        var type = await TypeAsync();
        var (client, _) = await ReaderAsync(type);
        var slug = await CreatedAsync(client, Home, null);
        await RunFieldChangeAsync(type);
        var refused = GrantKit.Name("lk");

        var created = await CreateAsync(client, refused, Home, [$"{type}.Ssn"]);
        var updated = await SaveAsync(client, slug, Home, [$"{type}.Ssn"]);

        created.StatusCode.Should().Be(HttpStatusCode.Conflict, await created.Content.ReadAsStringAsync(Ct));
        (await created.Content.ReadAsStringAsync(Ct)).Should().Contain("field change running");
        updated.StatusCode.Should().Be(HttpStatusCode.Conflict, await updated.Content.ReadAsStringAsync(Ct));
        (await StoredAsync(slug)).AllowedFields.Should().BeNull();
        using var scope = fixture.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<IQuerySession>().Query<Connector>().AnyAsync(c => c.Slug == refused, Ct))
            .Should().BeFalse();
    }

    [Fact]
    public async Task A_save_that_adds_nothing_is_not_held_up_by_a_field_change_on_a_type_it_names()
    {
        var type = await TypeAsync();
        var (client, _) = await ReaderAsync(type);
        var slug = await CreatedAsync(client, Home, [$"{type}.Ssn"]);
        await RunFieldChangeAsync(type);

        var response = await SaveAsync(client, slug, Home, [$"{type}.Ssn"]);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
    }
}
