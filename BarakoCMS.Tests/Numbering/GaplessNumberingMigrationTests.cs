using FluentAssertions;
using Npgsql;
using Xunit;

namespace BarakoCMS.Tests.Numbering;

/// <summary>
/// The 4.9.0 numbering file is rerunnable, so db-migrate can run it on a table that already exists.
/// On a database that enforces tenancy it has to leave the external number ledger with the tenant
/// policy, and on one that does not, without it.
/// </summary>
/// <remarks>
/// Run in a scratch schema standing in for public, the way TenantPolicyRestoreMigrationTests does,
/// so the policies it reads and writes are ones the test made.
/// </remarks>
[Collection("Integration1")]
public class GaplessNumberingMigrationTests
{
    private const string Policy = "tenant_id = current_setting('app.tenant_id', true)";
    private const string Ledger = "mt_doc_external_number_uses";

    private readonly IntegrationTestFixture _factory;

    public GaplessNumberingMigrationTests(IntegrationTestFixture factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task On_an_enforced_database_the_ledger_gets_the_policy_and_a_rerun_keeps_it()
    {
        var scratch = Scratch();
        await using var connection = await OpenAsync();
        try
        {
            await ExecuteAsync(connection, $"CREATE SCHEMA {scratch}");
            await DonorAsync(connection, scratch);
            var file = Scoped(await MigrationAsync(), scratch);

            await ExecuteAsync(connection, file);
            (await StateAsync(connection, scratch, Ledger)).Should().Be("enabled=true forced=true policies=1");

            await ExecuteAsync(connection, file);
            (await StateAsync(connection, scratch, Ledger)).Should().Be("enabled=true forced=true policies=1",
                "a second run on an enforced database must not take the policy off");
            (await PolicyAsync(connection, scratch, Ledger)).Should().Be(await PolicyAsync(connection, scratch, "mt_doc_things"),
                "the policy is copied from the table that has it, so db-assert finds the one it expects");
        }
        finally
        {
            await ExecuteAsync(connection, $"DROP SCHEMA IF EXISTS {scratch} CASCADE");
        }
    }

    [Fact]
    public async Task A_ledger_that_already_has_its_policy_is_left_alone()
    {
        var scratch = Scratch();
        await using var connection = await OpenAsync();
        try
        {
            await ExecuteAsync(connection, $"CREATE SCHEMA {scratch}");
            await DonorAsync(connection, scratch);
            var file = Scoped(await MigrationAsync(), scratch);
            await ExecuteAsync(connection, file);
            await ExecuteAsync(connection,
                $"DROP POLICY marten_tenant_isolation ON {scratch}.{Ledger}; "
              + $"CREATE POLICY marten_tenant_isolation ON {scratch}.{Ledger} USING ({Policy});");
            var before = await PolicyAsync(connection, scratch, Ledger);

            await ExecuteAsync(connection, file);

            (await PolicyAsync(connection, scratch, Ledger)).Should().Be(before,
                "a table that has the policy is not touched, even where its policy differs from the donor's");
        }
        finally
        {
            await ExecuteAsync(connection, $"DROP SCHEMA IF EXISTS {scratch} CASCADE");
        }
    }

    [Fact]
    public async Task Without_enforcement_the_ledger_has_no_policy_even_run_twice()
    {
        var scratch = Scratch();
        await using var connection = await OpenAsync();
        try
        {
            await ExecuteAsync(connection, $"CREATE SCHEMA {scratch}");
            var file = Scoped(await MigrationAsync(), scratch);

            await ExecuteAsync(connection, file);
            await ExecuteAsync(connection, file);

            (await StateAsync(connection, scratch, Ledger)).Should().Be("enabled=false forced=false policies=0",
                "with tenancy not enforced at the database this is what db-assert expects");
        }
        finally
        {
            await ExecuteAsync(connection, $"DROP SCHEMA IF EXISTS {scratch} CASCADE");
        }
    }

    private static string Scratch() => "numbering_check_" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>A conjoined table carrying the policy a host with database enforcement on gives it.</summary>
    private static Task DonorAsync(NpgsqlConnection connection, string scratch) =>
        ExecuteAsync(connection,
            $"CREATE TABLE {scratch}.mt_doc_things (tenant_id varchar NOT NULL DEFAULT '*DEFAULT*', id uuid NOT NULL, data jsonb NOT NULL, PRIMARY KEY (tenant_id, id)); "
          + $"CREATE POLICY marten_tenant_isolation ON {scratch}.mt_doc_things USING ({Policy}) WITH CHECK ({Policy}); "
          + $"ALTER TABLE {scratch}.mt_doc_things ENABLE ROW LEVEL SECURITY; "
          + $"ALTER TABLE {scratch}.mt_doc_things FORCE ROW LEVEL SECURITY;");

    /// <summary>The file with every object it names in public moved to the scratch schema.</summary>
    private static string Scoped(string sql, string scratch)
    {
        var scoped = sql
            .Replace("public.mt_doc_", $"{scratch}.mt_doc_")
            .Replace("public.barako_", $"{scratch}.barako_")
            .Replace("schemaname = 'public'", $"schemaname = '{scratch}'")
            .Replace("nspname = 'public'", $"nspname = '{scratch}'")
            .Replace("public.%I", $"{scratch}.%I");
        scoped.Should().NotContain("public.mt_doc_");
        scoped.Should().NotContain("public.barako_");
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

    private static async Task<string> MigrationAsync() =>
        await File.ReadAllTextAsync(Path.Combine(RepositoryRoot(), "migrations", "4.9.0", "gapless-numbering.sql"), Ct);

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "migrations")))
            directory = directory.Parent;

        directory.Should().NotBeNull("the test binary should sit under the repository");
        return directory!.FullName;
    }
}
