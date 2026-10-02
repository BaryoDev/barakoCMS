using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Infrastructure.Auth;
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

    // ---- the caller the store describes: tenant, wildcard, API key ---------------------------

    private async Task<string> TenantAsync()
    {
        var slug = $"sbc-{Guid.NewGuid():N}"[..14];
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = slug, IsActive = true });
        await session.SaveChangesAsync(Ct);
        return slug;
    }

    /// <summary>The same type and one Public entry of it, stored in the tenant named.</summary>
    private async Task<Guid> SeedInTenantAsync(string tenant, string type)
    {
        var id = Guid.NewGuid();
        await using var session = _factory.Services.GetRequiredService<IDocumentStore>().LightweightSession(tenant);
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = type,
            DisplayName = "Patient",
            Fields =
            [
                new FieldDefinition { Name = "Name", DisplayName = "Name", Type = "string" },
                new FieldDefinition { Name = "Notes", DisplayName = "Notes", Type = "string", Sensitivity = SensitivityLevel.Sensitive },
            ],
        });
        session.Store(new Content
        {
            Id = id,
            ContentType = type,
            Status = ContentStatus.Published,
            Data = new Dictionary<string, object> { ["Name"] = "Ana", ["Notes"] = Notes },
            CreatedAt = DateTime.UtcNow,
        });
        await session.SaveChangesAsync(Ct);
        return id;
    }

    /// <summary>A stored user with no role of their own, holding one role in each tenant named.</summary>
    private async Task<Guid> MemberAsync(params (string Tenant, Role Role)[] memberships)
    {
        var userId = Guid.NewGuid();
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new User
        {
            Id = userId,
            Username = $"sbc-{userId:n}",
            Email = $"sbc-{userId:n}@example.com",
            RoleIds = [],
        });
        foreach (var (tenant, role) in memberships)
        {
            session.Store(new Membership
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                TenantSlug = tenant,
                Status = MembershipStatus.Active,
                RoleIds = [role.Id],
            });
        }

        await session.SaveChangesAsync(Ct);
        return userId;
    }

    private HttpClient TenantClient(Guid userId, string tenant, string claimedRoleName)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(
                roles: [claimedRoleName],
                userId: userId.ToString(),
                additionalClaims: new Dictionary<string, string> { ["tenant"] = tenant }));
        client.DefaultRequestHeaders.Add("X-Tenant", tenant);
        return client;
    }

    [Fact]
    public async Task A_role_held_only_through_a_tenant_membership_opens_the_field_in_that_tenant_and_not_in_another()
    {
        var type = $"sbc{Guid.NewGuid():N}"[..16];
        var clinic = await TenantAsync();
        var school = await TenantAsync();
        var inClinic = await SeedInTenantAsync(clinic, type);
        var inSchool = await SeedInTenantAsync(school, type);
        var nurse = await StoreRoleAsync($"Nurse {Guid.NewGuid():N}", type, SystemCapabilities.ViewSensitive);
        var reader = await StoreRoleAsync($"Reader {Guid.NewGuid():N}", type);
        var userId = await MemberAsync((clinic, nurse), (school, reader));

        var atClinic = await ReadAsync(TenantClient(userId, clinic, nurse.Name), inClinic);
        Field(atClinic, "Notes").Should().Be(Notes,
            "the capability comes from the role the membership gives in this tenant");

        var atSchool = await ReadAsync(TenantClient(userId, school, reader.Name), inSchool);
        Field(atSchool, "Name").Should().Be("Ana", "the control: the same user reads the entry in the other tenant");
        Field(atSchool, "Notes").Should().Be("***",
            "and holds only the plain role there, so the role from the first tenant opens nothing");
    }

    [Fact]
    public async Task A_role_holding_the_wildcard_reads_Sensitive_and_Hidden_values_but_not_a_field_with_its_own_list()
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
        var everything = await CallerAsync(
            await StoreRoleAsync($"Operator {Guid.NewGuid():N}", type, SystemCapabilities.All));

        var entry = await ReadAsync(everything, await SeedEntryAsync(type));

        Field(entry, "Notes").Should().Be(Notes, "the wildcard satisfies view_sensitive");
        Field(entry, "Pin").Should().Be(Pin, "and view_hidden");
        Field(entry, "Salary").Should().Be("***",
            "a field's own list is not a capability, and only the seeded SuperAdmin role passes one it is not on");

        var hiddenEntry = await ReadAsync(everything, await SeedEntryAsync(type, SensitivityLevel.Hidden));
        Prop(hiddenEntry, "contentType", "ContentType").GetString().Should().Be(type);
    }

    [Fact]
    public async Task An_API_key_reads_as_its_owner_in_the_keys_tenant()
    {
        var type = $"sbc{Guid.NewGuid():N}"[..16];
        var clinic = await TenantAsync();
        var id = await SeedInTenantAsync(clinic, type);
        var nurse = await StoreRoleAsync($"Nurse {Guid.NewGuid():N}", type, SystemCapabilities.ViewSensitive);
        var reader = await StoreRoleAsync($"Reader {Guid.NewGuid():N}", type);

        var nurseKey = await KeyClientAsync(await MemberAsync((clinic, nurse)), clinic);
        var readerKey = await KeyClientAsync(await MemberAsync((clinic, reader)), clinic);

        Field(await ReadAsync(nurseKey, id), "Notes").Should().Be(Notes,
            "a key acts as its owner, and the owner's role in the key's tenant holds the capability");

        var plain = await ReadAsync(readerKey, id);
        Field(plain, "Name").Should().Be("Ana", "the control: this key reads the entry too");
        Field(plain, "Notes").Should().Be("***");
    }

    /// <summary>A client presenting a content:read key owned by the user, for the tenant named.</summary>
    private async Task<HttpClient> KeyClientAsync(Guid ownerId, string tenant)
    {
        var secret = "bcms_" + Guid.NewGuid().ToString("N");
        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new ApiKey
            {
                Id = Guid.NewGuid(),
                Name = "sensitivity",
                KeyHash = ApiKeyService.Hash(secret),
                Prefix = secret[..12],
                UserId = ownerId,
                TenantSlug = tenant,
                Scopes = ["content:read"],
            });
            await session.SaveChangesAsync(Ct);
        }

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        return client;
    }

    // ---- the write path ----------------------------------------------------------------------

    [Fact]
    public async Task A_Hidden_field_written_by_a_view_hidden_holder_is_kept_and_by_anyone_else_is_dropped()
    {
        var type = await SeedTypeAsync();
        var auditor = await CallerAsync(await StoreWriterRoleAsync(type, SystemCapabilities.ViewHidden));
        var clerk = await CallerAsync(await StoreWriterRoleAsync(type));

        var kept = await CreateAsync(auditor, type);
        kept.Should().ContainKey("Pin").WhoseValue.ToString().Should().Be(Pin,
            "a caller who may see a Hidden field may set it");
        kept.Keys.Should().NotContain("Notes", "view_hidden does not let a caller set a Sensitive field");

        var dropped = await CreateAsync(clerk, type);
        dropped.Should().ContainKey("Name", "the control: the entry was written");
        dropped.Keys.Should().NotContain("Pin", "a caller who may not see the field may not set it");
    }

    private async Task<Role> StoreWriterRoleAsync(string type, params string[] capabilities)
    {
        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = $"Writer {Guid.NewGuid():N}",
            SystemCapabilities = capabilities.ToList(),
            Permissions =
            [
                new ContentTypePermission
                {
                    ContentTypeSlug = type,
                    Read = new PermissionRule { Enabled = true },
                    Create = new PermissionRule { Enabled = true },
                },
            ],
        };
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(role);
        await session.SaveChangesAsync(Ct);
        return role;
    }

    /// <summary>Creates an entry sending every field, and returns what was stored.</summary>
    private async Task<Dictionary<string, object>> CreateAsync(HttpClient client, string type)
    {
        var created = await client.PostAsJsonAsync("/api/contents", new
        {
            ContentType = type,
            Data = new Dictionary<string, object> { ["Name"] = "Ana", ["Notes"] = Notes, ["Pin"] = Pin },
        }, Ct);
        var body = await created.Content.ReadAsStringAsync(Ct);
        created.StatusCode.Should().Be(HttpStatusCode.OK, body);
        var id = JsonDocument.Parse(body).RootElement.GetProperty("id").GetGuid();

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var stored = await session.LoadAsync<Content>(id, Ct);
        stored.Should().NotBeNull("the entry {0} was created", id);
        return stored!.Data;
    }
}
