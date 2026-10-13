using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// <c>migrations/5.0.0/role-tenancy.sql</c> gives every stored role an owner, copying a role held in
/// several tenants into each, and nobody's permissions change (#913).
/// </summary>
/// <remarks>
/// Run against a database of its own holding the tables as 4.9 left them, written with the
/// application's own serializer so the keys and the id format are the ones a real row has. The
/// shared database is not used: the file looks at every role, and other classes' roles are there.
/// </remarks>
[Collection("Sequential")]
public class RoleTenancyMigrationTests
{
    private readonly IntegrationTestFixture _fixture;

    public RoleTenancyMigrationTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Guid Accountant = Guid.Parse("00000000-0000-0000-0000-0000acc00001");

    private const string Schema =
        """
        create table public.mt_doc_roles (id uuid primary key, data jsonb not null,
            mt_last_modified timestamptz default transaction_timestamp(),
            mt_version uuid not null default (md5(random()::text || clock_timestamp()::text)::uuid),
            mt_dotnet_type varchar);
        create unique index mt_doc_roles_uidx_name on public.mt_doc_roles using btree ((data ->> 'Name'));
        create table public.mt_doc_memberships (id uuid primary key, data jsonb not null);
        create table public.mt_doc_users (id uuid primary key, data jsonb not null);
        create table public.mt_doc_contenttypedefinition (tenant_id varchar not null, id uuid not null,
            data jsonb not null, primary key (tenant_id, id));
        """;

    private string Json<T>(T document) =>
        _fixture.Services.GetRequiredService<IDocumentStore>().Options.Serializer().ToJson(document!);

    /// <summary>A role as 4.9 stored it: no TenantSlug and no IsPlatform.</summary>
    private string OldRole(Role role)
    {
        var node = JsonNode.Parse(Json(role))!.AsObject();
        node.Remove(nameof(Role.TenantSlug));
        node.Remove(nameof(Role.IsPlatform));
        return node.ToJsonString();
    }

    private static Guid CopyId(Guid role, string tenant) =>
        Guid.Parse(Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes($"{role}:{tenant}"))));

    private sealed record World(
        MigrationScratchDatabase Database, Guid Shared, Guid Global, Guid Unheld, string A, string B, string C,
        Guid MemberA, Guid MemberB, Guid RemovedC, Guid GlobalMemberA, Guid TypeA, Guid TypeB);

    private static Role Custom(Guid id, string name) => new()
    {
        Id = id,
        Name = name,
        Description = $"{name} role",
        SystemCapabilities = ["view_audit_log"],
        RequireMfa = name == "Nurse",
        Permissions = [new ContentTypePermission { ContentTypeSlug = "patient", Read = new PermissionRule { Enabled = true } }],
    };

    private async Task<World> WorldAsync()
    {
        var db = await MigrationScratchDatabase.CreateAsync(_fixture, started: false);
        await db.ExecuteAsync(Schema);

        var stamp = Guid.NewGuid().ToString("N")[..8];
        var (a, b, c) = ($"a-{stamp}", $"b-{stamp}", $"c-{stamp}");
        var shared = Guid.NewGuid();
        var global = Guid.NewGuid();
        var unheld = Guid.NewGuid();

        var roles = new[]
        {
            new Role { Id = SystemRoles.AdminRoleId, Name = "Admin", SystemCapabilities = ["manage_settings"] },
            new Role { Id = Accountant, Name = "Accountant" },
            Custom(shared, "Nurse"),
            Custom(global, "Auditor"),
            Custom(unheld, "Unused"),
        };
        foreach (var role in roles)
        {
            await InsertAsync(db, "mt_doc_roles", role.Id, OldRole(role), extra: ("mt_dotnet_type", "barakoCMS.Models.Role"));
        }

        var memberA = Guid.NewGuid();
        var memberB = Guid.NewGuid();
        var removedC = Guid.NewGuid();
        var globalMemberA = Guid.NewGuid();
        await MemberAsync(db, memberA, a, MembershipStatus.Active, shared, SystemRoles.AdminRoleId);
        await MemberAsync(db, memberB, b, MembershipStatus.Active, SystemRoles.AdminRoleId, shared);
        await MemberAsync(db, removedC, c, MembershipStatus.Removed, shared);
        await MemberAsync(db, globalMemberA, a, MembershipStatus.Active, global);

        var holder = new User { Id = Guid.NewGuid(), Username = "holder", Email = "holder@example.com", RoleIds = [global] };
        await InsertAsync(db, "mt_doc_users", holder.Id, Json(holder));

        var typeA = Guid.NewGuid();
        var typeB = Guid.NewGuid();
        await TypeAsync(db, a, typeA, shared);
        await TypeAsync(db, b, typeB, shared);

        return new World(db, shared, global, unheld, a, b, c, memberA, memberB, removedC, globalMemberA, typeA, typeB);
    }

    private async Task MemberAsync(MigrationScratchDatabase db, Guid id, string slug, MembershipStatus status, params Guid[] roleIds)
    {
        var membership = new Membership { Id = id, UserId = Guid.NewGuid(), TenantSlug = slug, Status = status, RoleIds = [.. roleIds] };
        await InsertAsync(db, "mt_doc_memberships", id, Json(membership));
    }

    private async Task TypeAsync(MigrationScratchDatabase db, string slug, Guid id, Guid visibleTo)
    {
        var type = new ContentTypeDefinition
        {
            Id = id,
            Name = "patient",
            DisplayName = "Patient",
            Fields =
            [
                new FieldDefinition { Name = "Name", DisplayName = "Name", Type = "string" },
                new FieldDefinition
                {
                    Name = "Notes", DisplayName = "Notes", Type = "string", Sensitivity = SensitivityLevel.Sensitive,
                    VisibleToRoles = [visibleTo.ToString(), "Doctor"],
                },
            ],
        };
        await using var connection = new NpgsqlConnection(db.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "insert into public.mt_doc_contenttypedefinition (tenant_id, id, data) values (@t, @id, @data::jsonb)", connection);
        command.Parameters.AddWithValue("t", slug);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("data", Json(type));
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static async Task InsertAsync(MigrationScratchDatabase db, string table, Guid id, string json, (string Column, string Value)? extra = null)
    {
        await using var connection = new NpgsqlConnection(db.ConnectionString);
        await connection.OpenAsync(Ct);
        var columns = extra is { } e ? $", {e.Column}" : string.Empty;
        var values = extra is not null ? ", @extra" : string.Empty;
        await using var command = new NpgsqlCommand($"insert into public.{table} (id, data{columns}) values (@id, @data::jsonb{values})", connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("data", json);
        if (extra is { } x)
        {
            command.Parameters.AddWithValue("extra", x.Value);
        }
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<List<string>> RunAsync(MigrationScratchDatabase db, string file = "role-tenancy.sql")
    {
        var sql = await File.ReadAllTextAsync(Path.Combine(RepositoryRoot(), "migrations", "5.0.0", file), Ct);
        var notices = new List<string>();
        await using var connection = new NpgsqlConnection(db.ConnectionString);
        connection.Notice += (_, e) => notices.Add(e.Notice.MessageText);
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using (var command = new NpgsqlCommand(sql, connection, transaction))
        {
            await command.ExecuteNonQueryAsync(Ct);
        }
        await transaction.CommitAsync(Ct);
        return notices;
    }

    private static async Task<Dictionary<Guid, JsonObject>> RowsAsync(MigrationScratchDatabase db, string table)
    {
        var rows = new Dictionary<Guid, JsonObject>();
        await using var connection = new NpgsqlConnection(db.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand($"select id, data::text from public.{table}", connection);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            rows[reader.GetGuid(0)] = JsonNode.Parse(reader.GetString(1))!.AsObject();
        }
        return rows;
    }

    /// <summary>What a membership's holder is allowed: the content of each role it holds, ids and owners left out.</summary>
    private static async Task<Dictionary<Guid, List<string>>> EffectiveAsync(MigrationScratchDatabase db)
    {
        var roles = await RowsAsync(db, "mt_doc_roles");
        var memberships = await RowsAsync(db, "mt_doc_memberships");
        return memberships.ToDictionary(
            m => m.Key,
            m => m.Value["RoleIds"]!.AsArray()
                .Select(id => roles[Guid.Parse(id!.GetValue<string>())])
                .Select(role =>
                {
                    var content = (JsonObject)role.DeepClone();
                    content.Remove("Id");
                    content.Remove(nameof(Role.TenantSlug));
                    content.Remove(nameof(Role.IsPlatform));
                    return content.ToJsonString();
                })
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToList());
    }

    private static List<string> VisibleTo(JsonObject type) =>
        type["Fields"]!.AsArray().Single(f => f!["Name"]!.GetValue<string>() == "Notes")!["VisibleToRoles"]!.AsArray()
            .Select(e => e!.GetValue<string>()).ToList();

    [Fact]
    public async Task Each_role_gets_an_owner_a_role_held_in_several_tenants_is_copied_and_no_member_loses_a_permission()
    {
        var world = await WorldAsync();
        await using var db = world.Database;
        var before = await EffectiveAsync(db);
        before.Should().HaveCount(4);
        before.Values.Should().OnlyContain(roles => roles.Count > 0);

        var notices = await RunAsync(db);
        notices.Should().Contain(n => n.Contains("gave 3 role(s) an owner and wrote 2 copy(ies)"));

        (await EffectiveAsync(db)).Should().BeEquivalentTo(before, o => o.WithStrictOrdering(),
            "every membership holds roles with the same content as before");

        var roles = await RowsAsync(db, "mt_doc_roles");
        roles.Should().HaveCount(7, "five roles and two copies");

        foreach (var seeded in new[] { SystemRoles.AdminRoleId, Accountant })
        {
            roles[seeded]["IsPlatform"]!.GetValue<bool>().Should().BeTrue();
            roles[seeded]["TenantSlug"].Should().BeNull();
        }

        roles[world.Shared]["TenantSlug"]!.GetValue<string>().Should().Be(world.A, "the first tenant holding it keeps the original");
        roles[world.Global]["IsPlatform"]!.GetValue<bool>().Should().BeTrue(
            "a role held globally grants in every tenant, so no one tenant's administrators may change it");
        roles[world.Global]["TenantSlug"].Should().BeNull();
        roles[world.Unheld]["TenantSlug"]!.GetValue<string>().Should().Be("default", "nobody holds it, so it stays with the default tenant");

        var sharedInB = CopyId(world.Shared, world.B);
        var sharedInC = CopyId(world.Shared, world.C);
        roles.Should().NotContainKey(CopyId(world.Global, world.A), "a platform role is reached from every tenant, so it is not copied");
        roles[sharedInB]["TenantSlug"]!.GetValue<string>().Should().Be(world.B);
        roles[sharedInB]["Id"]!.GetValue<string>().Should().Be(sharedInB.ToString(), "the copy's id inside the document matches its row");
        roles[sharedInC]["TenantSlug"]!.GetValue<string>().Should().Be(world.C, "a removed member's row keeps working if they are added back");

        var memberships = await RowsAsync(db, "mt_doc_memberships");
        Ids(memberships[world.MemberA]).Should().Equal(world.Shared, SystemRoles.AdminRoleId);
        Ids(memberships[world.MemberB]).Should().Equal(SystemRoles.AdminRoleId, sharedInB);
        Ids(memberships[world.RemovedC]).Should().Equal(sharedInC);
        Ids(memberships[world.GlobalMemberA]).Should().Equal(world.Global);

        var types = await RowsAsync(db, "mt_doc_contenttypedefinition");
        VisibleTo(types[world.TypeA]).Should().Equal(world.Shared.ToString(), "Doctor");
        VisibleTo(types[world.TypeB]).Should().Equal(sharedInB.ToString(), "Doctor");

        var users = await RowsAsync(db, "mt_doc_users");
        users.Values.Single()["RoleIds"]!.AsArray().Select(e => Guid.Parse(e!.GetValue<string>())).Should().Equal(world.Global);
    }

    private static List<Guid> Ids(JsonObject membership) =>
        membership["RoleIds"]!.AsArray().Select(e => Guid.Parse(e!.GetValue<string>())).ToList();

    [Fact]
    public async Task Running_it_twice_changes_nothing_the_second_time()
    {
        var world = await WorldAsync();
        await using var db = world.Database;
        await RunAsync(db);
        var tables = new[] { "mt_doc_roles", "mt_doc_memberships", "mt_doc_contenttypedefinition" };
        var first = new Dictionary<string, string>();
        foreach (var table in tables)
        {
            first[table] = JsonSerializer.Serialize((await RowsAsync(db, table)).OrderBy(r => r.Key).Select(r => r.Value.ToJsonString()));
        }
        first["mt_doc_roles"].Should().Contain("TenantSlug");

        var notices = await RunAsync(db);

        notices.Should().Contain(n => n.Contains("gave 0 role(s) an owner and wrote 0 copy(ies)"));
        foreach (var table in tables)
        {
            JsonSerializer.Serialize((await RowsAsync(db, table)).OrderBy(r => r.Key).Select(r => r.Value.ToJsonString()))
                .Should().Be(first[table], "{0} is as the first run left it", table);
        }
    }

    [Fact]
    public async Task The_index_it_creates_is_the_one_the_application_declares_and_the_name_index_is_gone()
    {
        _ = _fixture.Services.GetRequiredService<IDocumentStore>();
        var declared = await IndexDefinitionAsync(_fixture.ConnectionString, "mt_doc_roles_uidx_tenant_slug_name");
        declared.Should().NotBeNull("the application declares the index, so a started host has it");
        declared.Should().Contain("NULLS NOT DISTINCT", "platform roles have no tenant and still may not share a name");

        var world = await WorldAsync();
        await using var db = world.Database;
        (await IndexDefinitionAsync(db.ConnectionString, "mt_doc_roles_uidx_name")).Should().NotBeNull("the control: 4.9's index is there");

        await RunAsync(db);

        (await IndexDefinitionAsync(db.ConnectionString, "mt_doc_roles_uidx_tenant_slug_name")).Should().Be(declared);
        (await IndexDefinitionAsync(db.ConnectionString, "mt_doc_roles_uidx_name")).Should().BeNull();
    }

    private static Task<List<string>> RollBackAsync(MigrationScratchDatabase db) => RunAsync(db, "rollback-role-tenancy.sql");

    private static async Task<Dictionary<string, string>> TablesAsync(MigrationScratchDatabase db, bool ownerless = false)
    {
        var tables = new Dictionary<string, string>();
        foreach (var table in new[] { "mt_doc_roles", "mt_doc_memberships", "mt_doc_users", "mt_doc_contenttypedefinition" })
        {
            var rows = (await RowsAsync(db, table)).OrderBy(r => r.Key).Select(r =>
            {
                if (ownerless && table == "mt_doc_roles")
                {
                    r.Value.Remove(nameof(Role.TenantSlug));
                    r.Value.Remove(nameof(Role.IsPlatform));
                }
                return r.Value.ToJsonString();
            });
            tables[table] = JsonSerializer.Serialize(rows);
        }
        return tables;
    }

    [Fact]
    public async Task Rolling_back_folds_every_copy_into_its_role_and_puts_back_the_name_index_an_earlier_release_declares()
    {
        var world = await WorldAsync();
        await using var db = world.Database;
        var original = await IndexDefinitionAsync(db.ConnectionString, "mt_doc_roles_uidx_name");
        original.Should().NotBeNull("the control: 4.9's index is there");
        var before = await TablesAsync(db);
        var effective = await EffectiveAsync(db);
        effective.Should().HaveCount(4);

        await RunAsync(db);
        (await RowsAsync(db, "mt_doc_roles")).Should().HaveCount(7, "the control: the copies were written");

        var notices = await RollBackAsync(db);

        notices.Should().Contain(n => n.Contains("folded 2 copy(ies) back into their role, renamed 0 role(s)"));
        (await IndexDefinitionAsync(db.ConnectionString, "mt_doc_roles_uidx_name")).Should().Be(original);
        (await IndexDefinitionAsync(db.ConnectionString, "mt_doc_roles_uidx_tenant_slug_name")).Should().BeNull();

        var roles = await RowsAsync(db, "mt_doc_roles");
        roles.Should().HaveCount(5);
        foreach (var custom in new[] { world.Shared, world.Unheld })
        {
            roles[custom].ContainsKey(nameof(Role.TenantSlug)).Should().BeFalse("running role-tenancy.sql again works the owner out afresh");
            roles[custom].ContainsKey(nameof(Role.IsPlatform)).Should().BeFalse();
        }
        roles[world.Global]["IsPlatform"]!.GetValue<bool>().Should().BeTrue(
            "a role made a platform role because it was held globally stays one, and running role-tenancy.sql again skips it");
        roles[SystemRoles.AdminRoleId]["IsPlatform"]!.GetValue<bool>().Should().BeTrue("a platform role stays one");

        (await TablesAsync(db, ownerless: true)).Should().BeEquivalentTo(before,
            "every membership, account and field role list points at the role it held before the upgrade");
        (await EffectiveAsync(db)).Should().BeEquivalentTo(effective, o => o.WithStrictOrdering());
    }

    [Fact]
    public async Task Rolling_back_renames_a_role_that_shares_a_name_and_it_keeps_its_holders_and_permissions()
    {
        var world = await WorldAsync();
        await using var db = world.Database;
        await RunAsync(db);

        var sharedInB = CopyId(world.Shared, world.B);
        await db.ExecuteAsync(
            $"update public.mt_doc_roles set data = jsonb_set(data, '{{SystemCapabilities}}', '[\"view_audit_log\", \"manage_users\"]') where id = '{sharedInB}'");
        var made = Guid.NewGuid();
        await InsertAsync(db, "mt_doc_roles", made, Json(new Role { Id = made, Name = "Auditor", TenantSlug = world.C }),
            extra: ("mt_dotnet_type", "barakoCMS.Models.Role"));
        var memberC = Guid.NewGuid();
        await MemberAsync(db, memberC, world.C, MembershipStatus.Active, made);
        var typeC = Guid.NewGuid();
        await TypeAsync(db, world.C, typeC, made);
        await db.ExecuteAsync(
            $"update public.mt_doc_contenttypedefinition set data = jsonb_set(data, '{{Fields,1,VisibleToRoles}}', '[\"Auditor\"]') where id = '{typeC}'");
        var effective = await EffectiveAsync(db);
        effective.Should().HaveCount(5);

        var notices = await RollBackAsync(db);

        notices.Should().Contain(n => n.Contains("folded 1 copy(ies) back into their role, renamed 2 role(s)"));
        var roles = await RowsAsync(db, "mt_doc_roles");
        roles.Should().HaveCount(7, "five roles, the edited copy and the role made after the upgrade");
        roles.Values.Select(r => r["Name"]!.GetValue<string>()).Should().OnlyHaveUniqueItems();
        roles[sharedInB]["Name"]!.GetValue<string>().Should().Be($"Nurse ({world.B})");
        roles[made]["Name"]!.GetValue<string>().Should().Be($"Auditor ({world.C})");
        roles[world.Shared]["Name"]!.GetValue<string>().Should().Be("Nurse", "the role the copy came from keeps the name");
        roles[world.Global]["Name"]!.GetValue<string>().Should().Be("Auditor", "the platform role keeps the name");

        var memberships = await RowsAsync(db, "mt_doc_memberships");
        Ids(memberships[world.MemberB]).Should().Equal(SystemRoles.AdminRoleId, sharedInB);
        Ids(memberships[memberC]).Should().Equal(made);
        VisibleTo((await RowsAsync(db, "mt_doc_contenttypedefinition"))[typeC]).Should().Equal($"Auditor ({world.C})");

        (await EffectiveAsync(db)).Keys.Should().BeEquivalentTo(effective.Keys);
        foreach (var (membership, held) in await EffectiveAsync(db))
        {
            held.Select(WithoutName).Should().BeEquivalentTo(effective[membership].Select(WithoutName),
                "a renamed role keeps every permission it had");
        }

        (await IndexDefinitionAsync(db.ConnectionString, "mt_doc_roles_uidx_name")).Should().NotBeNull();
    }

    private static string WithoutName(string role)
    {
        var node = JsonNode.Parse(role)!.AsObject();
        node.Remove("Name");
        return node.ToJsonString();
    }

    [Fact]
    public async Task Rolling_back_twice_changes_nothing_the_second_time()
    {
        var world = await WorldAsync();
        await using var db = world.Database;
        await RunAsync(db);
        await RollBackAsync(db);
        var first = await TablesAsync(db);
        first["mt_doc_roles"].Should().Contain("Nurse");

        var notices = await RollBackAsync(db);

        notices.Should().Contain(n => n.Contains("folded 0 copy(ies) back into their role, renamed 0 role(s) that shared a name, and took the owner off 0 role(s)"));
        (await TablesAsync(db)).Should().BeEquivalentTo(first);
    }

    private static async Task<string?> IndexDefinitionAsync(string connectionString, string name)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "select indexdef from pg_indexes where schemaname = 'public' and indexname = @name", connection);
        command.Parameters.AddWithValue("name", name);
        return (string?)await command.ExecuteScalarAsync(Ct);
    }

    [Fact]
    public async Task The_seeder_makes_its_roles_platform_roles_whatever_was_stored()
    {
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        await using (var session = store.LightweightSession())
        {
            var admin = await session.LoadAsync<Role>(SystemRoles.AdminRoleId, Ct);
            admin!.IsPlatform = false;
            admin.TenantSlug = "default";
            session.Store(admin);
            await session.SaveChangesAsync(Ct);
        }

        await using (var session = store.LightweightSession())
        {
            await barakoCMS.Data.DataSeeder.SeedRolesAsync(session);
        }

        await using var read = store.QuerySession();
        foreach (var id in new[] { SystemRoles.SuperAdminRoleId, SystemRoles.AdminRoleId, SystemRoles.UserRoleId })
        {
            var role = await read.LoadAsync<Role>(id, Ct);
            role.Should().NotBeNull();
            role!.IsPlatform.Should().BeTrue("{0} is seeded for the whole deployment", role.Name);
            role.TenantSlug.Should().BeNull();
        }
    }

    [Fact]
    public async Task A_module_role_seeded_under_its_fixed_id_is_made_a_platform_role_and_one_found_by_name_is_not()
    {
        var seededId = Guid.NewGuid();
        var name = $"Seeded-{Guid.NewGuid():N}"[..20];
        var lookalike = $"Lookalike-{Guid.NewGuid():N}"[..20];
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new Role { Id = seededId, Name = name, TenantSlug = "default" });
        session.Store(new Role { Id = Guid.NewGuid(), Name = lookalike, TenantSlug = "default" });
        await session.SaveChangesAsync(Ct);

        var defaults = barakoCMS.Modules.CapabilityDefaults.For("view_audit_log")
            .GrantedTo(new SeededRole(seededId, name), new SeededRole(Guid.NewGuid(), lookalike));
        await defaults.GrantAsync(session, Ct);
        await session.SaveChangesAsync(Ct);

        var seeded = await session.LoadAsync<Role>(seededId, Ct);
        seeded!.IsPlatform.Should().BeTrue();
        seeded.TenantSlug.Should().BeNull();

        var other = await session.Query<Role>().SingleAsync(r => r.Name == lookalike, Ct);
        other.IsPlatform.Should().BeFalse("a role found by name may be an operator's own");
        other.TenantSlug.Should().Be("default");
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "migrations")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("the test binary should sit under the repository");
        return directory!.FullName;
    }
}
