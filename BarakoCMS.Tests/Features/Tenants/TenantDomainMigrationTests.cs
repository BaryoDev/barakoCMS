using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace BarakoCMS.Tests.Features.Tenants;

/// <summary>
/// The hand-applied tenant domains migration has to build the table Marten builds, and copy the
/// domains a deployment already serves.
/// </summary>
/// <remarks>
/// Production runs <c>AutoCreate.CreateOnly</c>: the table would be created on first boot but never
/// filled, so every domain a tenant listed before this release would stop routing. The file is run
/// into a scratch schema, so the shared tables are not touched.
/// </remarks>
[Collection("Integration1")]
public class TenantDomainMigrationTests
{
    private const string Table = "mt_doc_tenant_domains";

    private readonly IntegrationTestFixture _factory;

    public TenantDomainMigrationTests(IntegrationTestFixture factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_hand_applied_migration_builds_the_table_Marten_builds()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
            await store.Storage.Database.EnsureStorageExistsAsync(typeof(TenantDomain), Ct);
        }

        await using var connection = await OpenAsync();
        var scratch = await NewSchemaAsync(connection);
        try
        {
            var scoped = Scoped(scratch);
            await ExecuteAsync(connection, scoped);
            await ExecuteAsync(connection, scoped);

            var expected = await ShapeAsync(connection, "public", Table);
            var actual = await ShapeAsync(connection, scratch, Table);

            expected.Should().Contain(line => line.StartsWith("constraint ") && line.Contains("PRIMARY KEY (id)"),
                "the host is the id, and the primary key is what stops a second claim");
            actual.Should().Equal(expected, "the migration has to produce exactly what the module declares, or db-assert reports it");
        }
        finally
        {
            await DropAsync(connection, scratch);
        }
    }

    /// <summary>
    /// A domain the tenant listed before the table existed is copied as verified, so it keeps routing.
    /// The rows are read back through Marten's serializer and fed to the same builder the request path
    /// uses, so a field the SQL spells differently from the class fails here.
    /// </summary>
    [Fact]
    public async Task Domains_listed_before_the_table_existed_are_copied_as_verified_and_still_route()
    {
        IDocumentStore store;
        using (var scope = _factory.Services.CreateScope())
        {
            store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        }

        var serializer = store.Options.Serializer();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var solo = new Tenant { Id = Guid.NewGuid(), Slug = "solo-" + suffix, Name = "Solo", Domains = [$"Www.Solo-{suffix}.example.com", $"two-{suffix}.example.com"] };
        var rival = new Tenant { Id = Guid.NewGuid(), Slug = "rival-" + suffix, Name = "Rival", Domains = [$"two-{suffix}.example.com"] };
        var none = new Tenant { Id = Guid.NewGuid(), Slug = "none-" + suffix, Name = "None" };
        var edge = new Tenant { Id = Guid.NewGuid(), Slug = "edge-" + suffix, Name = "Edge", Domains = [$"Ported-{suffix}.example.com:8443", $"dotted-{suffix}.example.com.", $"both-{suffix}.example.com.:8080", $"UPPER-{suffix}.Example.COM", $"\ttabbed-{suffix}.example.com\t"] };

        await using var connection = await OpenAsync();
        var scratch = await NewSchemaAsync(connection);
        try
        {
            await ExecuteAsync(connection, $"CREATE TABLE {scratch}.mt_doc_tenants (id uuid PRIMARY KEY, data jsonb NOT NULL)");
            foreach (var tenant in new[] { solo, rival, none, edge })
            {
                await using var insert = new NpgsqlCommand($"INSERT INTO {scratch}.mt_doc_tenants (id, data) VALUES (@id, @data::jsonb)", connection);
                insert.Parameters.AddWithValue("id", tenant.Id);
                insert.Parameters.AddWithValue("data", serializer.ToJson(tenant));
                await insert.ExecuteNonQueryAsync(Ct);
            }

            var scoped = Scoped(scratch);
            await ExecuteAsync(connection, scoped);
            await ExecuteAsync(connection, scoped);

            var rows = new List<TenantDomain>();
            await using (var read = new NpgsqlCommand($"SELECT data FROM {scratch}.{Table}", connection))
            await using (var reader = await read.ExecuteReaderAsync(Ct))
            {
                while (await reader.ReadAsync(Ct))
                {
                    rows.Add(serializer.FromJson<TenantDomain>(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(reader.GetString(0)))));
                }
            }

            rows.Should().HaveCount(6, "the shared host is skipped, the others are copied once, and the second run adds nothing");
            rows.Select(r => r.Id).Should().BeEquivalentTo(new[]
            {
                $"solo-{suffix}.example.com",
                $"ported-{suffix}.example.com",
                $"dotted-{suffix}.example.com",
                $"both-{suffix}.example.com",
                $"upper-{suffix}.example.com",
                $"tabbed-{suffix}.example.com",
            }, "case, a port, a trailing dot and a tab are cut the way the request path cuts them");
            var row = rows.Single(r => r.Id == $"solo-{suffix}.example.com");
            row.Id.Should().Be($"solo-{suffix}.example.com", "the host is stored without www and in lower case");
            row.TenantSlug.Should().Be(solo.Slug);
            row.Status.Should().Be(TenantDomainStatus.Verified);
            row.VerifiedAt.Should().NotBeNull();
            row.Token.Should().MatchRegex("^[0-9a-f]{48}$");

            var map = TenantDomainSource.Build([solo, rival, none, edge], rows, NullLogger.Instance);
            map.Find($"solo-{suffix}.example.com").Should().Be(solo.Slug, "a backfilled domain stays routed");
            foreach (var host in edge.Domains)
            {
                var expected = TenantDomainMap.Normalise(host);
                rows.Select(r => r.Id).Should().Contain(expected, $"the SQL has to agree with Normalise for '{host}'");
            }

            map.Find($"two-{suffix}.example.com").Should().BeNull("two tenants listed it, so neither is given it");
        }
        finally
        {
            await DropAsync(connection, scratch);
        }
    }

    private static string Scoped(string scratch)
    {
        var sql = File.ReadAllText(Path.Combine(RepositoryRoot(), "migrations", "5.5.0", "1280-tenant-domains.sql"));
        var scoped = sql.Replace("public.", scratch + ".");
        scoped.Should().NotBe(sql, "the file should name its tables as public.<table>");
        return scoped;
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(_factory.ConnectionString);
        await connection.OpenAsync(Ct);
        return connection;
    }

    private static async Task<string> NewSchemaAsync(NpgsqlConnection connection)
    {
        var scratch = "tenant_domains_check_" + Guid.NewGuid().ToString("N")[..8];
        await ExecuteAsync(connection, $"CREATE SCHEMA {scratch}");
        return scratch;
    }

    private static Task DropAsync(NpgsqlConnection connection, string scratch) =>
        ExecuteAsync(connection, $"DROP SCHEMA IF EXISTS {scratch} CASCADE", CancellationToken.None);

    /// <summary>Columns, constraints and indexes of the table, with the schema name taken out.</summary>
    private static async Task<List<string>> ShapeAsync(NpgsqlConnection connection, string schema, string table)
    {
        var lines = new List<string>();
        await ReadAsync(connection,
            "select 'column ' || column_name || ' ' || data_type || ' ' || is_nullable || ' ' || coalesce(column_default, '') "
          + "from information_schema.columns where table_schema = @schema and table_name = @table order by column_name",
            schema, table, lines);
        await ReadAsync(connection,
            "select 'constraint ' || c.conname || ' ' || pg_get_constraintdef(c.oid) "
          + "from pg_constraint c join pg_class t on t.oid = c.conrelid join pg_namespace n on n.oid = t.relnamespace "
          + "where n.nspname = @schema and t.relname = @table order by c.conname",
            schema, table, lines);
        await ReadAsync(connection,
            "select 'index ' || indexdef from pg_indexes where schemaname = @schema and tablename = @table order by indexname",
            schema, table, lines);
        return lines.Select(line => line.Replace($"{schema}.{table}", table)).ToList();
    }

    private static async Task ReadAsync(NpgsqlConnection connection, string sql, string schema, string table, List<string> into)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("table", table);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            into.Add(reader.GetString(0));
        }
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken? ct = null)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(ct ?? Ct);
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
