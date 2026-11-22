using FluentAssertions;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// A superuser runs <c>migrations/4.6.0/sensitivity-by-capability.sql</c> with the search path pinned
/// to <c>pg_catalog, pg_temp</c> (<c>PGOPTIONS='-c search_path=pg_catalog,pg_temp'</c>), and it calls
/// only built in functions.
/// </summary>
/// <remarks>
/// With <c>public</c> on the path, the file's <c>to_jsonb(stored_role.id::text)</c> call finds
/// <c>public.to_jsonb(text)</c> (made by the app role, with an exact argument match) before the
/// built in one, so the test records each call in a table. With the pin the file resolves
/// <c>to_jsonb</c> in <c>pg_catalog</c> only: the table stays empty and the file does its work. The pin is
/// passed as the connection's <c>Options</c>, which is what <c>PGOPTIONS</c> sets. Each run has its own
/// database and role, since roles belong to the cluster.
/// </remarks>
[Collection("Integration1")]
public sealed class OwnerMigrationSearchPathTests(OwnerMigrationSearchPathTests.Postgres postgres)
    : IClassFixture<OwnerMigrationSearchPathTests.Postgres>
{
    private const string Pin = "-c search_path=pg_catalog,pg_temp";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public sealed class Postgres : IAsyncLifetime
    {
        private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("pgvector/pgvector:pg16")
            .WithUsername("postgres")
            .WithPassword("Su1-" + Guid.NewGuid().ToString("N"))
            .Build();

        public async ValueTask InitializeAsync() => await _container.StartAsync();

        public async ValueTask DisposeAsync() => await _container.DisposeAsync();

        public string ConnectionString(string database, string? options = null)
        {
            var builder = new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
            {
                Host = "127.0.0.1",
                Database = database,
                Pooling = false,
            };
            if (options is not null) builder.Options = options;
            return builder.ConnectionString;
        }
    }

    [Fact]
    public async Task The_pinned_migration_resolves_names_from_pg_catalog_only()
    {
        var control = await RunMigrationAsync(pinned: false);
        control.Calls.Should().BeGreaterThan(0,
            "with public on the search path the probe function in public matches the file's to_jsonb call, so this proves the probe can see a call");

        var pinned = await RunMigrationAsync(pinned: true);
        pinned.Calls.Should().Be(0,
            "with the search path pinned to pg_catalog, pg_temp a superuser's run of sensitivity-by-capability.sql resolves to_jsonb in pg_catalog only");
        pinned.VisibleToRoles.Should().Be(
            pinned.RoleId.ToString(),
            "the pinned run still does the file's work: the field lists the role by id, where it listed it by name");
    }

    private async Task<(int Calls, Guid RoleId, string? VisibleToRoles)> RunMigrationAsync(bool pinned)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var database = "owner_pin_" + suffix;
        var appRole = "app_pin_" + suffix;
        var roleId = Guid.NewGuid();

        await using (var admin = new NpgsqlConnection(postgres.ConnectionString("postgres")))
        {
            await admin.OpenAsync(Ct);
            await ExecuteAsync(admin, $"CREATE DATABASE {database}");
        }

        await using (var setup = new NpgsqlConnection(postgres.ConnectionString(database)))
        {
            await setup.OpenAsync(Ct);
            await ExecuteAsync(setup, $$"""
                CREATE ROLE {{appRole}} NOSUPERUSER NOBYPASSRLS;
                GRANT CREATE, USAGE ON SCHEMA public TO {{appRole}};
                CREATE TABLE public.mt_doc_roles (id uuid PRIMARY KEY, data jsonb NOT NULL);
                CREATE TABLE public.mt_doc_contenttypedefinition (id uuid PRIMARY KEY, data jsonb NOT NULL);
                CREATE TABLE public.probe_calls (n int NOT NULL);
                INSERT INTO public.mt_doc_roles VALUES ('{{roleId}}', '{"Name":"HR"}');
                INSERT INTO public.mt_doc_contenttypedefinition
                    VALUES (gen_random_uuid(), '{"Fields":[{"Name":"Salary","VisibleToRoles":["HR"]}]}');
                SET ROLE {{appRole}};
                CREATE FUNCTION public.to_jsonb(t text) RETURNS jsonb LANGUAGE plpgsql AS $probe$
                BEGIN
                    INSERT INTO public.probe_calls VALUES (1);
                    RETURN pg_catalog.to_jsonb(t);
                END
                $probe$;
                RESET ROLE;
                """);
        }

        var sql = await File.ReadAllTextAsync(
            Path.Combine(RepoPaths.Root, "migrations", "4.6.0", "sensitivity-by-capability.sql"), Ct);

        await using (var owner = new NpgsqlConnection(postgres.ConnectionString(database, pinned ? Pin : null)))
        {
            await owner.OpenAsync(Ct);
            await using var show = new NpgsqlCommand("SHOW search_path", owner);
            var path = (string?)await show.ExecuteScalarAsync(Ct);
            (path?.Replace(" ", "") == "pg_catalog,pg_temp").Should().Be(pinned, "the connection carries the search path this run is meant to have");
            await ExecuteAsync(owner, sql);
        }

        await using var read = new NpgsqlConnection(postgres.ConnectionString(database));
        await read.OpenAsync(Ct);
        await using var calls = new NpgsqlCommand("SELECT count(*) FROM public.probe_calls", read);
        var count = Convert.ToInt32(await calls.ExecuteScalarAsync(Ct));
        await using var field = new NpgsqlCommand(
            "SELECT data #>> '{Fields,0,VisibleToRoles,0}' FROM public.mt_doc_contenttypedefinition", read);
        return (count, roleId, (string?)await field.ExecuteScalarAsync(Ct));
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }
}
