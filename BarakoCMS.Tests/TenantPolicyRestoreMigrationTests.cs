using FluentAssertions;
using Npgsql;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// The 4.7.0 file puts the tenant policy back on a table an earlier upgrade file took it off, and
/// only on a database that enforces tenancy on its other tables.
/// </summary>
/// <remarks>
/// Run in a scratch schema standing in for public, so the policies it reads and writes are ones the
/// test made. The strip is done by running the shipped 4.3.0 collection-syncs file a second time,
/// the way its header allows, after the policy was put on the table as a host with database
/// enforcement puts it.
/// </remarks>
[Collection("Sequential")]
public class TenantPolicyRestoreMigrationTests
{
    private const string Policy = "tenant_id = current_setting('app.tenant_id', true)";

    private readonly IntegrationTestFixture _factory;

    public TenantPolicyRestoreMigrationTests(IntegrationTestFixture factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_policy_a_rerun_of_a_shipped_file_took_off_is_put_back()
    {
        var scratch = Scratch();
        await using var connection = await OpenAsync();
        try
        {
            await ExecuteAsync(connection, $"CREATE SCHEMA {scratch}");
            await DonorAsync(connection, scratch);

            var shipped = Scoped(await MigrationAsync("4.3.0", "collection-syncs.sql"), scratch);
            await ExecuteAsync(connection, shipped);
            await EnforceAsync(connection, scratch, "mt_doc_collection_syncs");
            (await StateAsync(connection, scratch, "mt_doc_collection_syncs")).Should().Be("enabled=true forced=true policies=1");

            await ExecuteAsync(connection, shipped);
            (await StateAsync(connection, scratch, "mt_doc_collection_syncs")).Should().Be(
                "enabled=false forced=false policies=0", "this is what a second run by hand leaves on an enforced database");

            var restore = Scoped(await MigrationAsync("4.7.0", "tenant-policy-restore.sql"), scratch);
            await ExecuteAsync(connection, restore);
            await ExecuteAsync(connection, restore);

            (await StateAsync(connection, scratch, "mt_doc_collection_syncs")).Should().Be("enabled=true forced=true policies=1");
            var copied = await PolicyAsync(connection, scratch, "mt_doc_collection_syncs");
            copied.Should().Be(await PolicyAsync(connection, scratch, "mt_doc_things"),
                "the policy is copied from the table that has it, so db-assert finds the one it expects");
            copied.Should().Contain("app.tenant_id");
        }
        finally
        {
            await ExecuteAsync(connection, $"DROP SCHEMA IF EXISTS {scratch} CASCADE");
        }
    }

    [Fact]
    public async Task Without_enforcement_on_the_other_tables_nothing_is_changed()
    {
        var scratch = Scratch();
        await using var connection = await OpenAsync();
        try
        {
            await ExecuteAsync(connection, $"CREATE SCHEMA {scratch}");
            await ExecuteAsync(connection, Scoped(await MigrationAsync("4.3.0", "collection-syncs.sql"), scratch));

            await ExecuteAsync(connection, Scoped(await MigrationAsync("4.7.0", "tenant-policy-restore.sql"), scratch));

            (await StateAsync(connection, scratch, "mt_doc_collection_syncs")).Should().Be(
                "enabled=false forced=false policies=0", "with tenancy not enforced at the database the table is as it should be");
        }
        finally
        {
            await ExecuteAsync(connection, $"DROP SCHEMA IF EXISTS {scratch} CASCADE");
        }
    }

    [Fact]
    public async Task A_table_that_still_has_its_policy_is_left_alone_and_the_rollback_changes_nothing()
    {
        var scratch = Scratch();
        await using var connection = await OpenAsync();
        try
        {
            await ExecuteAsync(connection, $"CREATE SCHEMA {scratch}");
            await DonorAsync(connection, scratch);
            await ExecuteAsync(connection, Scoped(await MigrationAsync("4.3.0", "collection-syncs.sql"), scratch));
            await ExecuteAsync(connection,
                $"CREATE POLICY marten_tenant_isolation ON {scratch}.mt_doc_collection_syncs USING ({Policy}); "
              + $"ALTER TABLE {scratch}.mt_doc_collection_syncs ENABLE ROW LEVEL SECURITY; "
              + $"ALTER TABLE {scratch}.mt_doc_collection_syncs FORCE ROW LEVEL SECURITY;");
            var before = await PolicyAsync(connection, scratch, "mt_doc_collection_syncs");

            await ExecuteAsync(connection, Scoped(await MigrationAsync("4.7.0", "tenant-policy-restore.sql"), scratch));
            await ExecuteAsync(connection, await MigrationAsync("4.7.0", "rollback-tenant-policy-restore.sql"));

            (await StateAsync(connection, scratch, "mt_doc_collection_syncs")).Should().Be("enabled=true forced=true policies=1");
            (await PolicyAsync(connection, scratch, "mt_doc_collection_syncs")).Should().Be(before,
                "a table that has the policy is not touched, even where its policy differs from the donor's");
        }
        finally
        {
            await ExecuteAsync(connection, $"DROP SCHEMA IF EXISTS {scratch} CASCADE");
        }
    }

    private static string Scratch() => "tenant_policy_check_" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>A conjoined table carrying the policy a host with database enforcement on gives it.</summary>
    private static Task DonorAsync(NpgsqlConnection connection, string scratch) =>
        ExecuteAsync(connection,
            $"CREATE TABLE {scratch}.mt_doc_things (tenant_id varchar NOT NULL DEFAULT '*DEFAULT*', id uuid NOT NULL, data jsonb NOT NULL, PRIMARY KEY (tenant_id, id)); "
          + $"CREATE POLICY marten_tenant_isolation ON {scratch}.mt_doc_things USING ({Policy}) WITH CHECK ({Policy}); "
          + $"ALTER TABLE {scratch}.mt_doc_things ENABLE ROW LEVEL SECURITY; "
          + $"ALTER TABLE {scratch}.mt_doc_things FORCE ROW LEVEL SECURITY;");

    private static Task EnforceAsync(NpgsqlConnection connection, string scratch, string table) =>
        ExecuteAsync(connection,
            $"CREATE POLICY marten_tenant_isolation ON {scratch}.{table} USING ({Policy}) WITH CHECK ({Policy}); "
          + $"ALTER TABLE {scratch}.{table} ENABLE ROW LEVEL SECURITY; "
          + $"ALTER TABLE {scratch}.{table} FORCE ROW LEVEL SECURITY;");

    /// <summary>The file with every object it names in public moved to the scratch schema.</summary>
    /// <remarks>
    /// Replaced piece by piece rather than every "public", which is also the role name the restore
    /// file writes for a policy that applies to everyone.
    /// </remarks>
    private static string Scoped(string sql, string scratch)
    {
        var scoped = sql
            .Replace("public.mt_doc_", $"{scratch}.mt_doc_")
            .Replace("schemaname = 'public'", $"schemaname = '{scratch}'")
            .Replace("nspname = 'public'", $"nspname = '{scratch}'")
            .Replace("'public.' ||", $"'{scratch}.' ||")
            .Replace("public.%I", $"{scratch}.%I");
        scoped.Should().NotBe(sql);
        scoped.Should().NotContain("public.mt_doc_");
        return scoped;
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(_factory.ConnectionString);
        await connection.OpenAsync(Ct);
        return connection;
    }

    private static async Task<string> StateAsync(NpgsqlConnection connection, string schema, string table)
    {
        await using var command = new NpgsqlCommand(
            "select 'enabled=' || t.relrowsecurity::text || ' forced=' || t.relforcerowsecurity::text "
          + "|| ' policies=' || (select count(*) from pg_policies p where p.schemaname = @schema and p.tablename = @table)::text "
          + "from pg_class t join pg_namespace n on n.oid = t.relnamespace where n.nspname = @schema and t.relname = @table",
            connection);
        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("table", table);
        return (string)(await command.ExecuteScalarAsync(Ct))!;
    }

    private static async Task<string> PolicyAsync(NpgsqlConnection connection, string schema, string table)
    {
        await using var command = new NpgsqlCommand(
            "select p.policyname || ' ' || p.permissive || ' ' || p.cmd || ' ' || array_to_string(p.roles, ',') "
          + "|| ' using ' || coalesce(p.qual, '') || ' check ' || coalesce(p.with_check, '') "
          + "from pg_policies p where p.schemaname = @schema and p.tablename = @table",
            connection);
        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("table", table);
        return (string)(await command.ExecuteScalarAsync(Ct))!;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static async Task<string> MigrationAsync(string version, string file) =>
        await File.ReadAllTextAsync(Path.Combine(RepositoryRoot(), "migrations", version, file), Ct);

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "migrations")))
            directory = directory.Parent;

        directory.Should().NotBeNull("the test binary should sit under the repository");
        return directory!.FullName;
    }
}
