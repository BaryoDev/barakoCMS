using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.Roles;

/// <summary>
/// The field sets on a role's rules, as <c>POST /api/roles</c> and <c>PUT /api/roles/{id}</c> check
/// and keep them.
/// </summary>
/// <remarks>
/// A set naming a field the type does not declare would allow nothing for that name, so a refusal
/// here protects nobody: it tells whoever saves the role. Keeping a set a request leaves out does
/// protect something, since a console that does not know the members would otherwise drop every
/// set each time it saved the role.
/// </remarks>
[Collection("Sequential")]
public class FieldSetRoleValidationTests
{
    private readonly IntegrationTestFixture _fixture;

    public FieldSetRoleValidationTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private async Task<HttpClient> AdminAsync()
    {
        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", await _fixture.StoredUserTokenAsync("SuperAdmin"));
        return client;
    }

    private async Task<string> TypeAsync()
    {
        var type = $"setrole{Guid.NewGuid().ToString("n")[..10]}";

        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(), Name = type, DisplayName = type,
            Fields =
            [
                new FieldDefinition { Name = "Attendance", DisplayName = "Attendance", Type = "string" },
                new FieldDefinition { Name = "Grade", DisplayName = "Grade", Type = "string" },
            ],
        });

        await session.SaveChangesAsync();
        return type;
    }

    /// <summary>A role body with one permission, its rules carrying the sets given.</summary>
    private static object Body(
        string type,
        string[]? readable = null,
        string[]? writable = null,
        string writableOn = "update",
        string? name = null)
    {
        object Rule(string slot) => new Dictionary<string, object?>
        {
            ["enabled"] = true,
            ["readableFields"] = slot == "read" ? readable : null,
            ["writableFields"] = slot == writableOn ? writable : null,
        };

        return new
        {
            name = name ?? $"FieldSets_{Guid.NewGuid():n}",
            description = "a role under test",
            permissions = new[]
            {
                new
                {
                    contentTypeSlug = type,
                    create = Rule("create"),
                    read = Rule("read"),
                    update = Rule("update"),
                    delete = Rule("delete"),
                },
            },
            systemCapabilities = Array.Empty<string>(),
        };
    }

    /// <summary>The body a console that does not know field sets sends: no such member anywhere.</summary>
    private static object Unaware(string type, string name) => new
    {
        name,
        description = "saved by a console that does not know field sets",
        permissions = new[]
        {
            new
            {
                contentTypeSlug = type,
                create = new { enabled = false },
                read = new { enabled = true },
                update = new { enabled = true },
                delete = new { enabled = false },
            },
        },
        systemCapabilities = Array.Empty<string>(),
    };

    private static async Task<Guid> ExistingRoleAsync(HttpClient admin)
    {
        var res = await admin.PostAsJsonAsync("/api/roles", new
        {
            name = $"Existing_{Guid.NewGuid():n}",
            description = "saved before the change under test",
            permissions = Array.Empty<object>(),
        });
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("id").GetGuid();
    }

    /// <summary>Sends the body as a create and as an update, and returns both answers.</summary>
    private static async Task<List<(string Route, HttpStatusCode Status, string Text)>> SaveAsync(
        HttpClient admin, Func<object> body)
    {
        var id = await ExistingRoleAsync(admin);

        var created = await admin.PostAsJsonAsync("/api/roles", body());
        var updated = await admin.PutAsJsonAsync($"/api/roles/{id}", body());

        return
        [
            ("POST", created.StatusCode, await created.Content.ReadAsStringAsync()),
            ("PUT", updated.StatusCode, await updated.Content.ReadAsStringAsync()),
        ];
    }

    private static void ShouldBeRefused(List<(string Route, HttpStatusCode Status, string Text)> answers, string saying)
    {
        answers.Should().HaveCount(2);

        foreach (var (route, status, text) in answers)
        {
            status.Should().Be(HttpStatusCode.BadRequest, "{0} answered {1}", route, text);
            text.Should().Contain(saying, "{0} says what is wrong", route);
        }
    }

    /// <summary>A role stored directly, as one saved before this check or in another tenant would be.</summary>
    private async Task<Guid> StoredRoleAsync(string type, List<string>? readable, List<string>? writable)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = $"Stored_{Guid.NewGuid():n}",
            Permissions =
            [
                new ContentTypePermission
                {
                    ContentTypeSlug = type,
                    Read = new PermissionRule { Enabled = true, ReadableFields = readable },
                    Update = new PermissionRule { Enabled = true, WritableFields = writable },
                },
            ],
        };

        session.Store(role);
        await session.SaveChangesAsync();
        return role.Id;
    }

    private async Task<Role> LoadRoleAsync(Guid id)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var role = await session.LoadAsync<Role>(id);
        role.Should().NotBeNull();
        return role!;
    }

    [Fact]
    public async Task Sets_naming_declared_fields_are_saved_and_read_back()
    {
        var admin = await AdminAsync();
        var type = await TypeAsync();

        var created = await admin.PostAsJsonAsync("/api/roles", Body(type, readable: ["Attendance", "Grade"], writable: ["Attendance"]));
        created.StatusCode.Should().Be(HttpStatusCode.OK, await created.Content.ReadAsStringAsync());

        using var saved = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var role = await LoadRoleAsync(saved.RootElement.GetProperty("id").GetGuid());

        role.Permissions.Should().HaveCount(1);
        role.Permissions[0].Read.ReadableFields.Should().BeEquivalentTo(new[] { "Attendance", "Grade" });
        role.Permissions[0].Update.WritableFields.Should().BeEquivalentTo(new[] { "Attendance" });
    }

    [Fact]
    public async Task A_set_naming_a_field_the_type_does_not_declare_is_refused()
    {
        var admin = await AdminAsync();
        var type = await TypeAsync();

        ShouldBeRefused(await SaveAsync(admin, () => Body(type, readable: ["Attendance", "Salary"])), "Salary");
        ShouldBeRefused(await SaveAsync(admin, () => Body(type, writable: ["grade"])), "grade");
    }

    [Fact]
    public async Task A_set_on_a_content_type_this_tenant_does_not_define_is_refused()
    {
        var admin = await AdminAsync();

        ShouldBeRefused(
            await SaveAsync(admin, () => Body($"missing{Guid.NewGuid():n}", readable: ["Attendance"])),
            "this tenant does not define");
    }

    [Theory]
    [InlineData("read")]
    [InlineData("delete")]
    public async Task A_writable_set_on_a_rule_other_than_create_or_update_is_refused(string slot)
    {
        var admin = await AdminAsync();
        var type = await TypeAsync();

        ShouldBeRefused(
            await SaveAsync(admin, () => Body(type, writable: ["Attendance"], writableOn: slot)),
            "writableFields is accepted on a Create or Update rule only");
    }

    [Fact]
    public async Task A_field_name_of_the_wrong_shape_is_refused_and_not_repeated_back()
    {
        var admin = await AdminAsync();
        var type = await TypeAsync();
        const string name = "<script>x</script>";

        var answers = await SaveAsync(admin, () => Body(type, readable: [name]));

        ShouldBeRefused(answers, "A field set holds field names");
        foreach (var (route, _, text) in answers)
            text.Should().NotContain("script", "{0} must not echo a name that failed the check", route);
    }

    [Fact]
    public async Task A_role_PUT_without_the_field_set_members_keeps_the_stored_sets()
    {
        var admin = await AdminAsync();
        var type = await TypeAsync();
        var id = await StoredRoleAsync(type, readable: ["Attendance"], writable: ["Attendance"]);

        var res = await admin.PutAsJsonAsync($"/api/roles/{id}", Unaware(type, $"Renamed_{Guid.NewGuid():n}"));
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());

        var role = await LoadRoleAsync(id);
        role.Name.Should().StartWith("Renamed_", "the PUT was applied, which is the control");
        role.Permissions.Should().HaveCount(1);
        role.Permissions[0].Read.ReadableFields.Should().BeEquivalentTo(new[] { "Attendance" });
        role.Permissions[0].Update.WritableFields.Should().BeEquivalentTo(new[] { "Attendance" });
    }

    [Fact]
    public async Task An_empty_set_removes_the_stored_set()
    {
        var admin = await AdminAsync();
        var type = await TypeAsync();
        var id = await StoredRoleAsync(type, readable: ["Attendance"], writable: ["Attendance"]);

        var res = await admin.PutAsJsonAsync($"/api/roles/{id}", Body(type, readable: [], writable: []));
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());

        var role = await LoadRoleAsync(id);
        role.Permissions.Should().HaveCount(1);
        role.Permissions[0].Read.ReadableFields.Should().BeNull();
        role.Permissions[0].Update.WritableFields.Should().BeNull();
    }

    [Fact]
    public async Task A_stored_set_on_a_type_this_tenant_lacks_does_not_stop_a_rename()
    {
        var admin = await AdminAsync();
        var elsewhere = $"elsewhere{Guid.NewGuid():n}";
        var id = await StoredRoleAsync(elsewhere, readable: ["Attendance"], writable: ["Attendance"]);

        var unaware = await admin.PutAsJsonAsync($"/api/roles/{id}", Unaware(elsewhere, $"Renamed_{Guid.NewGuid():n}"));
        unaware.StatusCode.Should().Be(HttpStatusCode.OK, await unaware.Content.ReadAsStringAsync());

        var resent = await admin.PutAsJsonAsync($"/api/roles/{id}",
            Body(elsewhere, readable: ["Attendance"], writable: ["Attendance"], name: $"Again_{Guid.NewGuid():n}"));
        resent.StatusCode.Should().Be(HttpStatusCode.OK, await resent.Content.ReadAsStringAsync());

        var changed = await admin.PutAsJsonAsync($"/api/roles/{id}",
            Body(elsewhere, readable: ["Grade"], name: $"Changed_{Guid.NewGuid():n}"));
        changed.StatusCode.Should().Be(HttpStatusCode.BadRequest, "a set the update changes is checked");
    }
}
