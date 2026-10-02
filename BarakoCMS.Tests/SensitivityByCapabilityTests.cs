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
/// Who may read a Sensitive or Hidden value is decided by what the caller's stored roles hold, and
/// a field's own role list is kept by role id.
/// </summary>
/// <remarks>
/// It used to be decided by two role names read back from the token: "HR" for Sensitive and
/// "SuperAdmin" for everything. A clinic's Nurse role could be granted nothing that opened a
/// Sensitive field, and renaming a role listed on a field changed who could read it.
///
/// Every caller here is a stored user holding a stored role under a random name, so no name can be
/// what decides an outcome. The token carries whatever names the test says, which is the point of
/// the tests that hand it "HR" and "SuperAdmin".
/// </remarks>
[Collection("Sequential")]
public class SensitivityByCapabilityTests
{
    private const string Notes = "patient notes 5521";
    private const string Pin = "pin 9034";
    private const string Salary = "salary 7718";

    private readonly IntegrationTestFixture _factory;

    public SensitivityByCapabilityTests(IntegrationTestFixture factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Name is Public, Notes is Sensitive, Pin is Hidden, and Salary is whatever is passed.</summary>
    private async Task<string> SeedTypeAsync(FieldDefinition? salary = null)
    {
        var type = $"sbc{Guid.NewGuid():N}"[..16];
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = type,
            DisplayName = "Patient",
            Fields =
            [
                new FieldDefinition { Name = "Name", DisplayName = "Name", Type = "string" },
                new FieldDefinition { Name = "Notes", DisplayName = "Notes", Type = "string", Sensitivity = SensitivityLevel.Sensitive },
                new FieldDefinition { Name = "Pin", DisplayName = "Pin", Type = "string", Sensitivity = SensitivityLevel.Hidden },
                salary ?? new FieldDefinition { Name = "Salary", DisplayName = "Salary", Type = "string" },
            ],
        });
        await session.SaveChangesAsync(Ct);
        return type;
    }

    private async Task<Guid> SeedEntryAsync(string type, SensitivityLevel level = SensitivityLevel.Public)
    {
        var id = Guid.NewGuid();
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new Content
        {
            Id = id,
            ContentType = type,
            Status = ContentStatus.Published,
            Sensitivity = level,
            Data = new Dictionary<string, object>
            {
                ["Name"] = "Ana",
                ["Notes"] = Notes,
                ["Pin"] = Pin,
                ["Salary"] = Salary,
            },
            CreatedAt = DateTime.UtcNow,
        });
        await session.SaveChangesAsync(Ct);
        return id;
    }

    /// <summary>A stored role that may read the type and holds the capabilities named.</summary>
    private async Task<Role> StoreRoleAsync(string name, string type, params string[] capabilities)
    {
        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = name,
            SystemCapabilities = capabilities.ToList(),
            Permissions =
            [
                new ContentTypePermission { ContentTypeSlug = type, Read = new PermissionRule { Enabled = true } },
            ],
        };
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(role);
        await session.SaveChangesAsync(Ct);
        return role;
    }

    /// <summary>A stored user holding the role, signed in with a token claiming the names given.</summary>
    private async Task<HttpClient> CallerAsync(Role role, params string[] claimedRoleNames)
    {
        var userId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new User
            {
                Id = userId,
                Username = $"sbc-{userId:n}",
                Email = $"sbc-{userId:n}@example.com",
                RoleIds = [role.Id],
            });
            await session.SaveChangesAsync(Ct);
        }

        return ClientFor(userId, claimedRoleNames.Length == 0 ? new[] { role.Name } : claimedRoleNames);
    }

    private HttpClient ClientFor(Guid userId, string[] claimedRoleNames)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(claimedRoleNames, userId.ToString()));
        return client;
    }

    private async Task<HttpClient> SuperAdminAsync()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await _factory.StoredUserTokenAsync("SuperAdmin"));
        return client;
    }

    private static async Task<JsonElement> ReadAsync(HttpClient client, Guid id)
    {
        var response = await client.GetAsync($"/api/contents/{id}", Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static JsonElement Prop(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var value))
                return value;
        }

        throw new Xunit.Sdk.XunitException($"none of [{string.Join(",", names)}] in {root}");
    }

    private static JsonElement Data(JsonElement root) => Prop(root, "data", "Data");

    private static string? Field(JsonElement root, string name) =>
        Data(root).TryGetProperty(name, out var value) ? value.GetString() : null;

    private async Task<FieldDefinition> StoredFieldAsync(string type, string field)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var definition = await session.Query<ContentTypeDefinition>().FirstOrDefaultAsync(d => d.Name == type, Ct);
        definition.Should().NotBeNull("the type {0} was stored", type);
        return definition!.Fields.Single(f => f.Name == field);
    }

    [Fact]
    public async Task A_custom_role_named_Nurse_holding_view_sensitive_reads_a_Sensitive_field()
    {
        var type = await SeedTypeAsync();
        var nurse = await CallerAsync(
            await StoreRoleAsync($"Nurse {Guid.NewGuid():N}", type, SystemCapabilities.ViewSensitive));

        var entry = await ReadAsync(nurse, await SeedEntryAsync(type));

        Field(entry, "Name").Should().Be("Ana", "the control: this caller reads the entry at all");
        Field(entry, "Notes").Should().Be(Notes, "view_sensitive is what a Sensitive field with no role list asks for");
        Field(entry, "Pin").Should().BeNull("view_sensitive does not reach a Hidden field");

        var sensitiveEntry = await ReadAsync(nurse, await SeedEntryAsync(type, SensitivityLevel.Sensitive));
        Field(sensitiveEntry, "Name").Should().Be("Ana", "an entry whose own level is Sensitive is open to the same capability");
        Field(sensitiveEntry, "Notes").Should().Be(Notes);
    }

    [Fact]
    public async Task A_role_holding_view_hidden_reads_a_Hidden_field_and_a_Hidden_entry()
    {
        var type = await SeedTypeAsync();
        var auditor = await CallerAsync(
            await StoreRoleAsync($"Auditor {Guid.NewGuid():N}", type, SystemCapabilities.ViewHidden));

        var entry = await ReadAsync(auditor, await SeedEntryAsync(type));

        Field(entry, "Pin").Should().Be(Pin, "view_hidden is what a Hidden field with no role list asks for");
        Field(entry, "Notes").Should().Be("***", "view_hidden does not include view_sensitive");

        var hiddenEntry = await ReadAsync(auditor, await SeedEntryAsync(type, SensitivityLevel.Hidden));
        Prop(hiddenEntry, "contentType", "ContentType").GetString().Should().Be(type, "the entry is not withheld from this caller");
        Field(hiddenEntry, "Name").Should().Be("Ana");
        Field(hiddenEntry, "Pin").Should().Be(Pin);
    }

    [Fact]
    public async Task The_role_names_in_a_token_open_nothing()
    {
        var type = await SeedTypeAsync();
        var plain = await CallerAsync(
            await StoreRoleAsync($"Clerk {Guid.NewGuid():N}", type),
            "HR", "SuperAdmin");

        var entry = await ReadAsync(plain, await SeedEntryAsync(type));

        Field(entry, "Name").Should().Be("Ana", "the control: this caller reads the entry at all");
        Field(entry, "Notes").Should().Be("***", "the stored role holds no view_sensitive, whatever the token calls it");
        Field(entry, "Pin").Should().BeNull("and it is not the seeded SuperAdmin role, whatever the token calls it");

        var hiddenEntry = await ReadAsync(plain, await SeedEntryAsync(type, SensitivityLevel.Hidden));
        Prop(hiddenEntry, "contentType", "ContentType").GetString().Should().Be("HIDDEN");
    }

    [Fact]
    public async Task Taking_the_capability_off_a_role_applies_on_the_next_request()
    {
        var type = await SeedTypeAsync();
        var role = await StoreRoleAsync($"Nurse {Guid.NewGuid():N}", type, SystemCapabilities.ViewSensitive);
        var nurse = await CallerAsync(role);
        var id = await SeedEntryAsync(type);

        Field(await ReadAsync(nurse, id), "Notes").Should().Be(Notes, "the capability is held, so the value is read");

        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            role.SystemCapabilities = [];
            session.Store(role);
            await session.SaveChangesAsync(Ct);
        }

        Field(await ReadAsync(nurse, id), "Notes").Should().Be("***",
            "the same token is still valid, and what it may read is the role as stored now");
    }

    [Fact]
    public async Task Renaming_a_role_listed_on_a_field_does_not_change_what_its_holders_see()
    {
        var type = await SeedTypeAsync();
        var id = await SeedEntryAsync(type);
        var payroll = await StoreRoleAsync($"Payroll {Guid.NewGuid():N}", type);
        var other = await StoreRoleAsync($"Clerk {Guid.NewGuid():N}", type);

        // By name, the way the console sends it. The answer names the role again, and what is
        // stored is its id.
        var admin = await SuperAdminAsync();
        var set = await admin.PutAsJsonAsync(
            $"/api/content-types/{type}/fields/Salary/sensitivity",
            new { sensitivity = "Sensitive", visibleToRoles = new[] { payroll.Name } },
            Ct);
        var answer = await set.Content.ReadAsStringAsync(Ct);
        set.StatusCode.Should().Be(HttpStatusCode.OK, answer);

        var listed = Prop(JsonDocument.Parse(answer).RootElement, "visibleToRoles", "VisibleToRoles")
            .EnumerateArray().Select(e => e.GetString()).ToList();
        listed.Should().HaveCount(1);
        listed.Should().Equal(payroll.Name);

        var stored = await StoredFieldAsync(type, "Salary");
        stored.VisibleToRoles.Should().HaveCount(1);
        stored.VisibleToRoles.Should().Equal(payroll.Id.ToString());

        var userId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new User
            {
                Id = userId,
                Username = $"sbc-{userId:n}",
                Email = $"sbc-{userId:n}@example.com",
                RoleIds = [payroll.Id],
            });
            await session.SaveChangesAsync(Ct);
        }

        Field(await ReadAsync(ClientFor(userId, [payroll.Name]), id), "Salary").Should().Be(Salary,
            "the holder of the listed role reads the field");
        Field(await ReadAsync(await CallerAsync(other), id), "Salary").Should().Be("***",
            "the control: a role that is not listed does not");

        var renamed = $"Finance {Guid.NewGuid():N}";
        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            payroll.Name = renamed;
            session.Store(payroll);
            await session.SaveChangesAsync(Ct);
        }

        // A token issued after the rename, carrying the new name the way a fresh sign-in would.
        Field(await ReadAsync(ClientFor(userId, [renamed]), id), "Salary").Should().Be(Salary,
            "the field lists the role by id, and the id did not change");

        var answered = await AnsweredRolesAsync(admin, type, "Salary");
        answered.Should().HaveCount(1);
        answered.Should().Equal(new[] { renamed }, "the content type list names the role as it is called now");
    }

    /// <summary>What <c>GET /api/content-types</c> answers for one field's role list.</summary>
    /// <remarks>
    /// Paged, because every test class shares this database and the type can be on any page.
    /// </remarks>
    private static async Task<List<string?>> AnsweredRolesAsync(HttpClient client, string type, string field)
    {
        for (var page = 1; ; page++)
        {
            var response = await client.GetAsync($"/api/content-types?page={page}&pageSize=100", Ct);
            var body = await response.Content.ReadAsStringAsync(Ct);
            response.StatusCode.Should().Be(HttpStatusCode.OK, body);

            using var document = JsonDocument.Parse(body);
            var items = document.RootElement.GetProperty("items").EnumerateArray().ToList();
            foreach (var item in items)
            {
                if (item.GetProperty("name").GetString() != type)
                    continue;

                var found = item.GetProperty("fields").EnumerateArray()
                    .Single(f => f.GetProperty("name").GetString() == field);
                return found.GetProperty("visibleToRoles").EnumerateArray().Select(e => e.GetString()).ToList();
            }

            if (items.Count < 100)
                throw new Xunit.Sdk.XunitException($"{type} is not in the content type list");
        }
    }

    [Fact]
    public async Task A_definition_that_still_lists_a_role_by_name_shows_the_field_to_that_role()
    {
        var roleName = $"Payroll {Guid.NewGuid():N}";
        var type = await SeedTypeAsync(new FieldDefinition
        {
            Name = "Salary",
            DisplayName = "Salary",
            Type = "string",
            Sensitivity = SensitivityLevel.Sensitive,
            VisibleToRoles = [roleName],
        });
        var id = await SeedEntryAsync(type);
        var payroll = await CallerAsync(await StoreRoleAsync(roleName, type));
        var other = await CallerAsync(await StoreRoleAsync($"Clerk {Guid.NewGuid():N}", type));

        Field(await ReadAsync(payroll, id), "Salary").Should().Be(Salary,
            "a definition stored before ids were lists the name, and the caller's stored role carries it");
        Field(await ReadAsync(other, id), "Salary").Should().Be("***");
    }

    [Fact]
    public async Task A_field_with_its_own_role_list_is_closed_to_a_role_that_only_holds_the_capability()
    {
        var listed = await StoreRoleAsync($"Payroll {Guid.NewGuid():N}", "unused");
        var type = await SeedTypeAsync(new FieldDefinition
        {
            Name = "Salary",
            DisplayName = "Salary",
            Type = "string",
            Sensitivity = SensitivityLevel.Sensitive,
            VisibleToRoles = [listed.Id.ToString()],
        });
        var nurse = await CallerAsync(
            await StoreRoleAsync($"Nurse {Guid.NewGuid():N}", type, SystemCapabilities.ViewSensitive));

        var entry = await ReadAsync(nurse, await SeedEntryAsync(type));

        Field(entry, "Notes").Should().Be(Notes, "the control: the capability opens the field that lists nobody");
        Field(entry, "Salary").Should().Be("***", "a field's own list replaces the default, it does not add to it");
    }

    [Fact]
    public async Task Creating_a_type_stores_a_listed_role_name_as_that_roles_id()
    {
        var type = $"sbcnew{Guid.NewGuid():N}"[..16];
        var role = await StoreRoleAsync($"Registrar {Guid.NewGuid():N}", type);
        var nobody = $"Nobody {Guid.NewGuid():N}";

        var created = await (await SuperAdminAsync()).PostAsJsonAsync("/api/content-types", new
        {
            name = type,
            displayName = "Student",
            fields = new object[]
            {
                new { name = "Name", type = "string" },
                new { name = "Grades", type = "string", sensitivity = "Sensitive", visibleToRoles = new[] { role.Name, nobody } },
            },
        }, Ct);
        created.StatusCode.Should().Be(HttpStatusCode.OK, await created.Content.ReadAsStringAsync(Ct));

        var stored = await StoredFieldAsync(type, "Grades");
        var expected = new[] { role.Id.ToString(), nobody };
        stored.VisibleToRoles.Should().HaveCount(2);
        stored.VisibleToRoles.Should().Equal(
            expected,
            "a name a role carries is stored as that role's id, and a name no role carries is kept");
    }

    [Fact]
    public async Task Adding_a_field_stores_a_listed_role_name_as_that_roles_id()
    {
        var type = await SeedTypeAsync();
        var role = await StoreRoleAsync($"Registrar {Guid.NewGuid():N}", type);

        var added = await (await SuperAdminAsync()).PostAsJsonAsync($"/api/content-types/{type}/fields", new
        {
            fieldName = "Grades",
            type = "string",
            sensitivity = "Sensitive",
            visibleToRoles = new[] { role.Name },
        }, Ct);
        added.StatusCode.Should().Be(HttpStatusCode.OK, await added.Content.ReadAsStringAsync(Ct));

        var stored = await StoredFieldAsync(type, "Grades");
        stored.VisibleToRoles.Should().HaveCount(1);
        stored.VisibleToRoles.Should().Equal(role.Id.ToString());
    }
}
