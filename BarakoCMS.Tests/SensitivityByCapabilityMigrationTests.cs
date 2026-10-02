using System.Text.Json;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// The hand-applied 4.6.0 data migration keeps who reads Sensitive fields the same, and its
/// rollback puts back what an earlier release reads.
/// </summary>
/// <remarks>
/// Before 4.6.0 the role named HR read a Sensitive field by its name, and a field listed roles by
/// name. <c>migrations/4.6.0/sensitivity-by-capability.sql</c> gives every role named HR the
/// capability that replaced the name, and rewrites listed names to role ids.
///
/// The documents are written by Marten, so the JSON the file meets is the JSON a deployment holds.
/// They are then copied into a scratch schema and the file is run there with its two table names
/// repointed, because the file as written changes every role and every content type in the
/// database, and this database is shared with every other test class.
/// </remarks>
[Collection("Sequential")]
public class SensitivityByCapabilityMigrationTests
{
    private const string Roles = "mt_doc_roles";
    private const string Types = "mt_doc_contenttypedefinition";

    private readonly IntegrationTestFixture _factory;

    public SensitivityByCapabilityMigrationTests(IntegrationTestFixture factory) => _factory = factory;

    [Fact]
    public async Task The_migration_grants_HR_the_capability_and_lists_roles_by_id_and_the_rollback_undoes_both()
    {
        var ct = TestContext.Current.CancellationToken;
        var scratch = "sensitivity_check_" + Guid.NewGuid().ToString("N")[..8];
        var up = await ScopedAsync("sensitivity-by-capability.sql", scratch, ct);
        var down = await ScopedAsync("rollback-sensitivity-by-capability.sql", scratch, ct);

        var hr = new Role { Id = Guid.NewGuid(), Name = $"Becomes HR {Guid.NewGuid():N}", SystemCapabilities = [SystemCapabilities.ViewAuditLog] };
        var payroll = new Role { Id = Guid.NewGuid(), Name = $"Payroll {Guid.NewGuid():N}" };
        var ghost = $"Ghost {Guid.NewGuid():N}";
        var type = new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = $"sbcmig{Guid.NewGuid():N}"[..16],
            DisplayName = "Staff",
            Fields =
            [
                new FieldDefinition { Name = "Name", DisplayName = "Name", Type = "string" },
                new FieldDefinition
                {
                    Name = "Salary",
                    DisplayName = "Salary",
                    Type = "string",
                    Sensitivity = SensitivityLevel.Sensitive,
                    VisibleToRoles = [payroll.Name, ghost],
                },
            ],
        };

        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(hr);
            session.Store(payroll);
            session.Store(type);
            await session.SaveChangesAsync(ct);
        }

        await using var connection = new NpgsqlConnection(_factory.ConnectionString);
        await connection.OpenAsync(ct);
        try
        {
            await ExecuteAsync(connection, $"CREATE SCHEMA {scratch}", ct);
            await ExecuteAsync(connection,
                $"CREATE TABLE {scratch}.{Roles} AS SELECT * FROM public.{Roles} "
              + $"WHERE id IN ('{hr.Id}', '{payroll.Id}')", ct);
            await ExecuteAsync(connection,
                $"CREATE TABLE {scratch}.{Types} AS SELECT * FROM public.{Types} WHERE id = '{type.Id}'", ct);

            // The name the migration keys on. Role names are uniquely indexed in public, and the
            // fixture's own HR role may or may not still carry it, so it is given here.
            await ExecuteAsync(connection,
                $"UPDATE {scratch}.{Roles} SET data = jsonb_set(data, '{{Name}}', '\"HR\"') WHERE id = '{hr.Id}'", ct);

            (await CapabilitiesAsync(connection, scratch, hr.Id, ct)).Should().Equal(
                new[] { SystemCapabilities.ViewAuditLog }, "the control: the role starts without the capability");
            (await SalaryRolesAsync(connection, scratch, type.Id, ct)).Should().Equal(
                new[] { payroll.Name, ghost }, "the control: the field starts out listing names");

            await ExecuteAsync(connection, up, ct);
            await ExecuteAsync(connection, up, ct);

            var granted = await CapabilitiesAsync(connection, scratch, hr.Id, ct);
            granted.Should().HaveCount(2);
            granted.Should().Equal(
                new[] { SystemCapabilities.ViewAuditLog, SystemCapabilities.ViewSensitive },
                "the role named HR keeps what it held and gains the capability once, however often the file runs");
            (await CapabilitiesAsync(connection, scratch, payroll.Id, ct)).Should().BeEmpty(
                "a role under any other name is given nothing");

            var listed = await SalaryRolesAsync(connection, scratch, type.Id, ct);
            listed.Should().HaveCount(2);
            listed.Should().Equal(
                new[] { payroll.Id.ToString(), ghost },
                "a name a role carries becomes that role's id, and a name no role carries is kept");

            await ExecuteAsync(connection, down, ct);
            await ExecuteAsync(connection, down, ct);

            var afterRollback = await CapabilitiesAsync(connection, scratch, hr.Id, ct);
            afterRollback.Should().HaveCount(1);
            afterRollback.Should().Equal(
                new[] { SystemCapabilities.ViewAuditLog }, "an earlier release does not know view_sensitive");

            var named = await SalaryRolesAsync(connection, scratch, type.Id, ct);
            named.Should().HaveCount(2);
            named.Should().Equal(
                new[] { payroll.Name, ghost }, "an earlier release matches the list against role names");
        }
        finally
        {
            await ExecuteAsync(connection, $"DROP SCHEMA IF EXISTS {scratch} CASCADE", CancellationToken.None);
        }
    }

    /// <summary>The file with its two tables moved into the scratch schema.</summary>
    private static async Task<string> ScopedAsync(string file, string scratch, CancellationToken ct)
    {
        var sql = await File.ReadAllTextAsync(Path.Combine(RepositoryRoot(), "migrations", "4.6.0", file), ct);

        sql.Should().Contain($"public.{Roles}", "{0} should name the roles table as public.{1}", file, Roles);
        sql.Should().Contain($"public.{Types}", "{0} should name the content types table as public.{1}", file, Types);
        return sql.Replace($"public.{Roles}", $"{scratch}.{Roles}").Replace($"public.{Types}", $"{scratch}.{Types}");
    }

    private static async Task<List<string?>> CapabilitiesAsync(
        NpgsqlConnection connection, string schema, Guid roleId, CancellationToken ct)
    {
        var data = await DataAsync(connection, $"{schema}.{Roles}", roleId, ct);
        return data.GetProperty("SystemCapabilities").EnumerateArray().Select(e => e.GetString()).ToList();
    }

    private static async Task<List<string?>> SalaryRolesAsync(
        NpgsqlConnection connection, string schema, Guid typeId, CancellationToken ct)
    {
        var data = await DataAsync(connection, $"{schema}.{Types}", typeId, ct);
        var salary = data.GetProperty("Fields").EnumerateArray().Single(f => f.GetProperty("Name").GetString() == "Salary");
        return salary.GetProperty("VisibleToRoles").EnumerateArray().Select(e => e.GetString()).ToList();
    }

    private static async Task<JsonElement> DataAsync(
        NpgsqlConnection connection, string table, Guid id, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand($"select data::text from {table} where id = @id", connection);
        command.Parameters.AddWithValue("id", id);
        var text = (string?)await command.ExecuteScalarAsync(ct);
        text.Should().NotBeNull("the row {0} was copied into {1}", id, table);
        return JsonDocument.Parse(text!).RootElement.Clone();
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(ct);
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
