using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests;

/// <summary>
/// Real end-to-end tests for schema-driven sensitivity: they seed a content type whose SSN field is
/// Hidden and BirthDay field is Sensitive, sign in as different roles, GET/LIST through the HTTP
/// pipeline, and assert what each role actually receives.
/// </summary>
[Collection("Sequential")]
public class SensitivityIntegrationTests
{
    private readonly IntegrationTestFixture _factory;
    private readonly HttpClient _client;

    public SensitivityIntegrationTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    // A unique content type whose SSN is Hidden (SuperAdmin only) and BirthDay is Sensitive (HR+).
    private async Task<string> SeedSchema()
    {
        var contentType = $"emp_{Guid.NewGuid():N}";
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        using var session = store.LightweightSession();
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = contentType,
            DisplayName = "Employee",
            Fields = new List<FieldDefinition>
            {
                new() { Name = "Name", Type = "string", Sensitivity = SensitivityLevel.Public },
                new() { Name = "BirthDay", Type = "datetime", Sensitivity = SensitivityLevel.Sensitive },
                new() { Name = "SSN", Type = "string", Sensitivity = SensitivityLevel.Hidden },
            },
        });
        await session.SaveChangesAsync();
        return contentType;
    }

    // A user who can READ contentType, holding a token that carries tokenRole for the sensitivity
    // filter's IsInRole checks. The DB role has a unique name (Role.Name is uniquely indexed) and
    // grants read via an explicit permission, so it is decoupled from the token's semantic role.
    private async Task<string> SetupReader(string tokenRole, string contentType)
    {
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        using var session = store.LightweightSession();

        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = $"dbrole_{Guid.NewGuid():N}",
            Permissions = new List<ContentTypePermission>
            {
                new()
                {
                    ContentTypeSlug = contentType,
                    Read = new PermissionRule { Enabled = true },
                    Create = new PermissionRule { Enabled = false },
                    Update = new PermissionRule { Enabled = false },
                    Delete = new PermissionRule { Enabled = false },
                },
            },
        };
        session.Store(role);
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = $"user_{Guid.NewGuid()}",
            Email = $"{Guid.NewGuid()}@example.com",
            RoleIds = new List<Guid> { role.Id },
        };
        session.Store(user);
        await session.SaveChangesAsync();

        return _factory.CreateToken(new[] { tokenRole }, user.Id.ToString());
    }

    // Like SetupReader but also grants create + update, for write-path tests.
    private async Task<string> SetupWriter(string tokenRole, string contentType)
    {
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        using var session = store.LightweightSession();

        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = $"dbrole_{Guid.NewGuid():N}",
            Permissions = new List<ContentTypePermission>
            {
                new()
                {
                    ContentTypeSlug = contentType,
                    Read = new PermissionRule { Enabled = true },
                    Create = new PermissionRule { Enabled = true },
                    Update = new PermissionRule { Enabled = true },
                    Delete = new PermissionRule { Enabled = false },
                },
            },
        };
        session.Store(role);
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = $"user_{Guid.NewGuid()}",
            Email = $"{Guid.NewGuid()}@example.com",
            RoleIds = new List<Guid> { role.Id },
        };
        session.Store(user);
        await session.SaveChangesAsync();
        return _factory.CreateToken(new[] { tokenRole }, user.Id.ToString());
    }

    private async Task<Guid> SeedRecord(string contentType, SensitivityLevel level)
    {
        var id = Guid.NewGuid();
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        using var session = store.LightweightSession();
        session.Store(new Content
        {
            Id = id,
            ContentType = contentType,
            Sensitivity = level,
            Data = new Dictionary<string, object>
            {
                { "Name", "Juan Dela Cruz" },
                { "BirthDay", "1990-05-15" },
                { "SSN", "123-45-6789" },
            },
            CreatedAt = DateTime.UtcNow,
        });
        await session.SaveChangesAsync();
        return id;
    }

    private async Task<(HttpStatusCode Status, JsonElement Root)> Get(string token, Guid id)
    {
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var res = await _client.GetAsync($"/api/contents/{id}");
        var body = await res.Content.ReadAsStringAsync();
        var root = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body).RootElement.Clone();
        return (res.StatusCode, root);
    }

    private static JsonElement Prop(JsonElement root, params string[] names)
    {
        foreach (var n in names)
            if (root.TryGetProperty(n, out var v)) return v;
        throw new Xunit.Sdk.XunitException($"none of [{string.Join(",", names)}] in {root}");
    }

    private static JsonElement Data(JsonElement root) => Prop(root, "data", "Data");

    [Fact]
    public async Task SuperAdmin_sees_all_sensitive_fields()
    {
        var ct = await SeedSchema();
        var token = await SetupReader("SuperAdmin", ct);
        var id = await SeedRecord(ct, SensitivityLevel.Sensitive);
        var (status, root) = await Get(token, id);
        status.Should().Be(HttpStatusCode.OK);
        Data(root).GetProperty("SSN").GetString().Should().Be("123-45-6789");
        Data(root).GetProperty("BirthDay").GetString().Should().Contain("1990-05-15");
    }

    [Fact]
    public async Task HR_sees_birthday_but_not_ssn()
    {
        var ct = await SeedSchema();
        var token = await SetupReader("HR", ct);
        var id = await SeedRecord(ct, SensitivityLevel.Sensitive);
        var (status, root) = await Get(token, id);
        status.Should().Be(HttpStatusCode.OK);
        Data(root).TryGetProperty("SSN", out _).Should().BeFalse("SSN is Hidden (SuperAdmin only)");
        Data(root).GetProperty("BirthDay").GetString().Should().Contain("1990-05-15", "HR may see the Sensitive BirthDay");
    }

    [Fact]
    public async Task PlainUser_on_public_record_gets_ssn_removed_and_birthday_masked()
    {
        var ct = await SeedSchema();
        var token = await SetupReader($"Viewer_{Guid.NewGuid():N}", ct);
        var id = await SeedRecord(ct, SensitivityLevel.Public);
        var (status, root) = await Get(token, id);
        status.Should().Be(HttpStatusCode.OK);
        Data(root).TryGetProperty("SSN", out _).Should().BeFalse("Hidden field removed for a plain reader");
        Data(root).GetProperty("BirthDay").GetString().Should().Be("***", "Sensitive field redacted for a plain reader");
        Data(root).GetProperty("Name").GetString().Should().Be("Juan Dela Cruz", "Public field stays visible");
    }

    [Fact]
    public async Task PlainUser_on_sensitive_document_sees_nothing()
    {
        var ct = await SeedSchema();
        var token = await SetupReader($"Viewer_{Guid.NewGuid():N}", ct);
        var id = await SeedRecord(ct, SensitivityLevel.Sensitive);
        var (status, root) = await Get(token, id);
        status.Should().Be(HttpStatusCode.OK);
        Data(root).EnumerateObject().Count().Should().Be(0, "a Sensitive document is fully withheld from a plain reader");
    }

    [Fact]
    public async Task PlainUser_on_hidden_document_is_blanked()
    {
        var ct = await SeedSchema();
        var token = await SetupReader($"Viewer_{Guid.NewGuid():N}", ct);
        var id = await SeedRecord(ct, SensitivityLevel.Hidden);
        var (status, root) = await Get(token, id);
        status.Should().Be(HttpStatusCode.OK);
        Prop(root, "contentType", "ContentType").GetString().Should().Be("HIDDEN");
        Data(root).EnumerateObject().Count().Should().Be(0);
    }

    [Fact]
    public async Task SuperAdmin_on_hidden_document_still_sees_it()
    {
        var ct = await SeedSchema();
        var token = await SetupReader("SuperAdmin", ct);
        var id = await SeedRecord(ct, SensitivityLevel.Hidden);
        var (status, root) = await Get(token, id);
        status.Should().Be(HttpStatusCode.OK);
        Data(root).GetProperty("Name").GetString().Should().Be("Juan Dela Cruz");
    }

    [Fact]
    public async Task List_masks_fields_for_plain_reader()
    {
        var ct = await SeedSchema();
        var token = await SetupReader($"Viewer_{Guid.NewGuid():N}", ct);
        await SeedRecord(ct, SensitivityLevel.Public);

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var res = await _client.GetAsync($"/api/contents?contentType={ct}&page=1&pageSize=50");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadAsStringAsync();
        // The record's SSN must not appear anywhere in the list payload.
        body.Should().NotContain("123-45-6789", "List must scrub the Hidden SSN, not just Get");
    }

    // ---- write-path protection ----

    [Fact]
    public async Task PlainUser_cannot_inject_hidden_field_on_create()
    {
        var ct = await SeedSchema();
        var viewer = await SetupWriter($"Viewer_{Guid.NewGuid():N}", ct);
        var admin = await SetupReader("SuperAdmin", ct);

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", viewer);
        var create = await _client.PostAsJsonAsync("/api/contents", new barakoCMS.Features.Content.Create.Request
        {
            ContentType = ct,
            Data = new Dictionary<string, object> { { "Name", "X" }, { "SSN", "999-99-9999" } },
        });
        create.StatusCode.Should().Be(HttpStatusCode.OK);
        var id = JsonDocument.Parse(await create.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        var (status, root) = await Get(admin, id);
        status.Should().Be(HttpStatusCode.OK);
        Data(root).TryGetProperty("SSN", out _).Should().BeFalse("a plain writer cannot inject a Hidden field");
        Data(root).GetProperty("Name").GetString().Should().Be("X");
    }

    [Fact]
    public async Task PlainUser_cannot_overwrite_hidden_field_on_update()
    {
        var ct = await SeedSchema();
        var viewer = await SetupWriter($"Viewer_{Guid.NewGuid():N}", ct);
        var admin = await SetupWriter("SuperAdmin", ct);

        // Create the record via the API (as SuperAdmin) so it has a proper event stream to update.
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", admin);
        var create = await _client.PostAsJsonAsync("/api/contents", new barakoCMS.Features.Content.Create.Request
        {
            ContentType = ct,
            Data = new Dictionary<string, object> { { "Name", "Original" }, { "SSN", "123-45-6789" } },
        });
        create.StatusCode.Should().Be(HttpStatusCode.OK);
        var id = JsonDocument.Parse(await create.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", viewer);
        var upd = await _client.PutAsJsonAsync($"/api/contents/{id}", new barakoCMS.Features.Content.Update.Request
        {
            Id = id,
            Status = ContentStatus.Draft,
            Version = 0, // bypass optimistic check
            Data = new Dictionary<string, object> { { "Name", "Changed" }, { "SSN", "999-99-9999" } },
        });
        upd.StatusCode.Should().Be(HttpStatusCode.OK);

        var (_, root) = await Get(admin, id);
        Data(root).GetProperty("SSN").GetString().Should().Be("123-45-6789", "a plain writer cannot overwrite a Hidden field");
        Data(root).GetProperty("Name").GetString().Should().Be("Changed", "but non-sensitive fields update normally");
    }

    [Fact]
    public async Task HR_can_write_sensitive_field_but_not_hidden()
    {
        var ct = await SeedSchema();
        var hr = await SetupWriter("HR", ct);
        var admin = await SetupReader("SuperAdmin", ct);

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", hr);
        var create = await _client.PostAsJsonAsync("/api/contents", new barakoCMS.Features.Content.Create.Request
        {
            ContentType = ct,
            Data = new Dictionary<string, object> { { "Name", "Y" }, { "BirthDay", "2001-02-03" }, { "SSN", "111-11-1111" } },
        });
        create.StatusCode.Should().Be(HttpStatusCode.OK);
        var id = JsonDocument.Parse(await create.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        var (_, root) = await Get(admin, id);
        Data(root).GetProperty("BirthDay").GetString().Should().Contain("2001-02-03", "HR may write the Sensitive BirthDay");
        Data(root).TryGetProperty("SSN", out _).Should().BeFalse("HR still cannot write the Hidden SSN");
    }
}
