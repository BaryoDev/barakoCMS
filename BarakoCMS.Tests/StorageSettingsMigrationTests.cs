using BarakoCMS.Files.Storage;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// migrations/5.0.0/files-storage-settings.sql creates the table the Files module declares, so
/// db-assert finds nothing outstanding, and it follows the database on the tenant policy.
/// </summary>
/// <remarks>
/// Run in a scratch schema standing in for public, the way GaplessNumberingMigrationTests does. The
/// table the host created in public is the expectation.
/// </remarks>
[Collection("Integration4")]
public class StorageSettingsMigrationTests
{
    private const string Table = "mt_doc_storage_settings";
    private const string Policy = "tenant_id = current_setting('app.tenant_id', true)";

    private readonly IntegrationTestFixture _factory;

    public StorageSettingsMigrationTests(IntegrationTestFixture factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_file_creates_the_table_the_module_declares()
    {
        // The host's own table, from its first start.
        using (var scope = _factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IQuerySession>()
                .LoadAsync<StorageSettings>(StorageSettings.SingletonId, Ct);
        }

        var scratch = Scratch();
        await using var connection = await OpenAsync();
        try
        {
            await ExecuteAsync(connection, $"CREATE SCHEMA {scratch}");
            var file = Scoped(await MigrationAsync(), scratch);

            await ExecuteAsync(connection, file);
            await ExecuteAsync(connection, file);

            var declared = await ShapeAsync(connection, "public");
            declared.Should().NotBeEmpty("the host created the table, so there is something to compare with");
            (await ShapeAsync(connection, scratch)).Should().Equal(declared);
            (await StateAsync(connection, scratch)).Should().Be("enabled=false forced=false policies=0",
                "with tenancy not enforced at the database this is what db-assert expects");
        }
        finally
        {
            await ExecuteAsync(connection, $"DROP SCHEMA IF EXISTS {scratch} CASCADE");
        }
    }

    [Fact]
    public async Task On_an_enforced_database_the_table_gets_the_policy_and_a_rerun_keeps_it()
    {
        var scratch = Scratch();
        await using var connection = await OpenAsync();
        try
        {
            await ExecuteAsync(connection, $"CREATE SCHEMA {scratch}");
            await ExecuteAsync(connection,
                $"CREATE TABLE {scratch}.mt_doc_things (tenant_id varchar NOT NULL DEFAULT '*DEFAULT*', id uuid NOT NULL, data jsonb NOT NULL, PRIMARY KEY (tenant_id, id)); "
              + $"CREATE POLICY marten_tenant_isolation ON {scratch}.mt_doc_things USING ({Policy}) WITH CHECK ({Policy}); "
              + $"ALTER TABLE {scratch}.mt_doc_things ENABLE ROW LEVEL SECURITY; "
              + $"ALTER TABLE {scratch}.mt_doc_things FORCE ROW LEVEL SECURITY;");
            var file = Scoped(await MigrationAsync(), scratch);

            await ExecuteAsync(connection, file);
            await ExecuteAsync(connection, file);

            (await StateAsync(connection, scratch)).Should().Be("enabled=true forced=true policies=1",
                "a second run on an enforced database must not take the policy off");
        }
        finally
        {
            await ExecuteAsync(connection, $"DROP SCHEMA IF EXISTS {scratch} CASCADE");
        }
    }

    private static string Scratch() => "storage_check_" + Guid.NewGuid().ToString("N")[..8];

    private static string Scoped(string sql, string scratch)
    {
        var scoped = sql
            .Replace("public.mt_doc_", $"{scratch}.mt_doc_")
            .Replace("schemaname = 'public'", $"schemaname = '{scratch}'")
            .Replace("nspname = 'public'", $"nspname = '{scratch}'")
            .Replace("public.%I", $"{scratch}.%I");
        scoped.Should().NotContain("public.mt_doc_");
        return scoped;
    }

    /// <summary>Each column with its type, nullability and default, then the primary key.</summary>
    private static async Task<List<string>> ShapeAsync(NpgsqlConnection connection, string schema)
    {
        var shape = new List<string>();
        await using (var columns = new NpgsqlCommand(
            "select column_name || ' ' || data_type || ' ' || is_nullable || ' ' || coalesce(column_default, '') "
          + "from information_schema.columns where table_schema = @schema and table_name = @table order by column_name",
            connection))
        {
            columns.Parameters.AddWithValue("schema", schema);
            columns.Parameters.AddWithValue("table", Table);
            await using var reader = await columns.ExecuteReaderAsync(Ct);
            while (await reader.ReadAsync(Ct))
            {
                shape.Add(reader.GetString(0));
            }
        }

        await using var key = new NpgsqlCommand(
            "select c.conname || ' ' || pg_get_constraintdef(c.oid) from pg_constraint c "
          + "join pg_class t on t.oid = c.conrelid join pg_namespace n on n.oid = t.relnamespace "
          + "where n.nspname = @schema and t.relname = @table and c.contype = 'p'",
            connection);
        key.Parameters.AddWithValue("schema", schema);
        key.Parameters.AddWithValue("table", Table);
        if (await key.ExecuteScalarAsync(Ct) is string primary)
        {
            shape.Add(primary);
        }

        return shape;
    }

    private static async Task<string> StateAsync(NpgsqlConnection connection, string schema)
    {
        await using var command = new NpgsqlCommand(
            "select 'enabled=' || t.relrowsecurity::text || ' forced=' || t.relforcerowsecurity::text "
          + "|| ' policies=' || (select count(*) from pg_policies p where p.schemaname = @schema and p.tablename = @table)::text "
          + "from pg_class t join pg_namespace n on n.oid = t.relnamespace where n.nspname = @schema and t.relname = @table",
            connection);
        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("table", Table);
        return (string)(await command.ExecuteScalarAsync(Ct))!;
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(_factory.ConnectionString);
        await connection.OpenAsync(Ct);
        return connection;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static async Task<string> MigrationAsync()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "migrations")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("the test binary should sit under the repository");
        return await File.ReadAllTextAsync(Path.Combine(directory!.FullName, "migrations", "5.0.0", "files-storage-settings.sql"), Ct);
    }
}
