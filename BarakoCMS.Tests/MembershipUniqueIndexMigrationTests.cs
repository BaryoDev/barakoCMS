using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests;

/// <summary>
/// A person holds one membership per tenant: the index says so, the 4.7.0 file adds it to an
/// existing database or refuses, and two adds at once do not store two rows.
/// </summary>
/// <remarks>
/// The file is run against a copy of the memberships table in a scratch schema, so the rows it
/// refuses on are ones the test put there and the shared table is never touched.
/// </remarks>
[Collection("Sequential")]
public class MembershipUniqueIndexMigrationTests
{
    private const string Table = "mt_doc_memberships";
    private const string Index = "mt_doc_memberships_uidx_user_id_tenant_slug";

    private readonly IntegrationTestFixture _factory;
    private static int _ipCounter;

    public MembershipUniqueIndexMigrationTests(IntegrationTestFixture factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_migration_matches_the_index_Marten_creates()
    {
        await EnsureTableAsync();
        await using var connection = await OpenAsync();
        var live = new List<string>();
        await ReadAsync(connection,
            "select indexdef from pg_indexes where schemaname = 'public' and tablename = @table and indexname = @index",
            live);

        live.Should().HaveCount(1, "core declares the unique index on UserId and TenantSlug under this name");

        var sql = await MigrationAsync("membership-unique-user-tenant.sql");
        Regex.IsMatch(sql, @"CREATE\s+UNIQUE\s+INDEX", RegexOptions.IgnoreCase).Should().BeTrue();
        live[0].Should().Contain("UNIQUE");
        Expression(sql).Should().Be(Expression(live[0]),
            "the file has to build the index Marten builds, or a start-up schema assertion asks to drop and recreate it");
    }

    [Fact]
    public async Task The_migration_refuses_and_changes_nothing_when_two_rows_share_a_user_and_tenant()
    {
        await EnsureTableAsync();
        var scratch = "membership_check_" + Guid.NewGuid().ToString("N")[..8];
        var sql = Scoped(await MigrationAsync("membership-unique-user-tenant.sql"), scratch);

        await using var connection = await OpenAsync();
        try
        {
            await CopyTableAsync(connection, scratch);
            var user = Guid.NewGuid();
            await InsertAsync(connection, scratch, user, "acme");
            await InsertAsync(connection, scratch, user, "acme");
            await InsertAsync(connection, scratch, user, "other");

            var run = async () => await ExecuteAsync(connection, sql);

            (await run.Should().ThrowAsync<PostgresException>())
                .Which.MessageText.Should().Contain("1 user and tenant pair(s) have more than one membership row");
            (await IndexesAsync(connection, scratch)).Should().BeEmpty("the file refused before building anything");
            (await CountAsync(connection, scratch)).Should().Be(3, "no row was deleted to make the index fit");
        }
        finally
        {
            await ExecuteAsync(connection, $"DROP SCHEMA IF EXISTS {scratch} CASCADE");
        }
    }

    [Fact]
    public async Task The_migration_builds_the_index_twice_over_and_the_rollback_drops_it()
    {
        await EnsureTableAsync();
        var scratch = "membership_check_" + Guid.NewGuid().ToString("N")[..8];
        var up = Scoped(await MigrationAsync("membership-unique-user-tenant.sql"), scratch);
        var down = Scoped(await MigrationAsync("rollback-membership-unique-user-tenant.sql"), scratch);

        await using var connection = await OpenAsync();
        try
        {
            await CopyTableAsync(connection, scratch);
            var user = Guid.NewGuid();
            await InsertAsync(connection, scratch, user, "acme");
            await InsertAsync(connection, scratch, user, "other");

            await ExecuteAsync(connection, up);
            await ExecuteAsync(connection, up);

            var indexes = await IndexesAsync(connection, scratch);
            indexes.Should().HaveCount(1);
            indexes[0].Should().Contain(Index).And.Contain("UNIQUE");

            var duplicate = async () => await InsertAsync(connection, scratch, user, "acme");
            (await duplicate.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23505");

            await ExecuteAsync(connection, down);
            await ExecuteAsync(connection, down);
            (await IndexesAsync(connection, scratch)).Should().BeEmpty();
        }
        finally
        {
            await ExecuteAsync(connection, $"DROP SCHEMA IF EXISTS {scratch} CASCADE");
        }
    }

    [Fact]
    public async Task A_second_membership_for_the_same_user_and_tenant_is_refused()
    {
        var user = Guid.NewGuid();
        var slug = "uniq-" + Guid.NewGuid().ToString("N")[..10];

        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new Membership { Id = Guid.NewGuid(), UserId = user, TenantSlug = slug, Status = MembershipStatus.Active });
            await session.SaveChangesAsync(Ct);
        }

        using var second = _factory.Services.CreateScope();
        var write = second.ServiceProvider.GetRequiredService<IDocumentSession>();
        write.Store(new Membership { Id = Guid.NewGuid(), UserId = user, TenantSlug = slug, Status = MembershipStatus.Active });

        var save = async () => await write.SaveChangesAsync(Ct);

        await save.Should().ThrowAsync<Exception>();
        (await MembershipsAsync(user, slug)).Should().HaveCount(1);
    }

    [Fact]
    public async Task Adding_one_person_from_several_requests_at_once_stores_one_membership()
    {
        var slug = await TenantAsync();
        var admin = await AdminOfAsync(slug);
        var email = $"race-{Guid.NewGuid():n}@example.com";
        var person = await UserAsync(email);

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            admin.PostAsJsonAsync("/api/tenants/members", new { email, roleIds = new[] { SystemRoles.UserRoleId } }, Ct)));

        responses.Should().HaveCount(6);
        responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.OK || r.StatusCode == HttpStatusCode.Conflict,
            "a request that lost the race is told so, not answered 500");
        responses.Should().Contain(r => r.StatusCode == HttpStatusCode.OK);
        (await MembershipsAsync(person, slug)).Should().HaveCount(1);
    }

    private async Task EnsureTableAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        await store.Storage.Database.EnsureStorageExistsAsync(typeof(Membership), Ct);
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(_factory.ConnectionString);
        await connection.OpenAsync(Ct);
        return connection;
    }

    private static async Task CopyTableAsync(NpgsqlConnection connection, string scratch) =>
        await ExecuteAsync(connection,
            $"CREATE SCHEMA {scratch}; CREATE TABLE {scratch}.{Table} (LIKE public.{Table} INCLUDING DEFAULTS);");

    private static async Task InsertAsync(NpgsqlConnection connection, string scratch, Guid user, string tenant)
    {
        await using var command = new NpgsqlCommand(
            $"insert into {scratch}.{Table} (id, data) values (@id, jsonb_build_object('UserId', @user::text, 'TenantSlug', @tenant::text))",
            connection);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("user", user.ToString());
        command.Parameters.AddWithValue("tenant", tenant);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<List<string>> IndexesAsync(NpgsqlConnection connection, string scratch)
    {
        var found = new List<string>();
        await using var command = new NpgsqlCommand(
            "select indexdef from pg_indexes where schemaname = @schema and tablename = @table", connection);
        command.Parameters.AddWithValue("schema", scratch);
        command.Parameters.AddWithValue("table", Table);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
            found.Add(reader.GetString(0));
        return found;
    }

    private static async Task<long> CountAsync(NpgsqlConnection connection, string scratch)
    {
        await using var command = new NpgsqlCommand($"select count(*) from {scratch}.{Table}", connection);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    private static async Task ReadAsync(NpgsqlConnection connection, string sql, List<string> into)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("table", Table);
        command.Parameters.AddWithValue("index", Index);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
            into.Add(reader.GetString(0));
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    /// <summary>The file with the table, and so the index named after it, moved into a scratch schema.</summary>
    private static string Scoped(string sql, string scratch)
    {
        var scoped = sql.Replace($"public.{Table}", $"{scratch}.{Table}")
            .Replace("n.nspname = 'public'", $"n.nspname = '{scratch}'");
        scoped.Should().NotBe(sql, "the file should name the table as public.{0}", Table);
        return scoped;
    }

    /// <summary>The indexed expression, whitespace collapsed so formatting is not a difference.</summary>
    private static string Expression(string sql)
    {
        var match = Regex.Match(sql, @"CREATE\s+UNIQUE\s+INDEX\s+(?:IF\s+NOT\s+EXISTS\s+)?" + Index + @"\s+ON\s+\S+\s+USING\s+btree\s*(\(.*\))\s*;?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        match.Success.Should().BeTrue($"the statement should create {Index}: {sql}");
        return Regex.Replace(match.Groups[1].Value, @"\s+", " ").Trim();
    }

    private static async Task<string> MigrationAsync(string file) =>
        await File.ReadAllTextAsync(Path.Combine(RepositoryRoot(), "migrations", "4.7.0", file), Ct);

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "migrations")))
            directory = directory.Parent;

        directory.Should().NotBeNull("the test binary should sit under the repository");
        return directory!.FullName;
    }

    private async Task<IReadOnlyList<Membership>> MembershipsAsync(Guid userId, string slug)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return await session.Query<Membership>()
            .Where(m => m.UserId == userId && m.TenantSlug == slug)
            .ToListAsync(Ct);
    }

    private async Task<string> TenantAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var slug = $"uniq-{Guid.NewGuid():N}"[..16].ToLowerInvariant();
        session.Store(new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = slug, IsActive = true });
        await session.SaveChangesAsync(Ct);
        return slug;
    }

    private async Task<Guid> UserAsync(string email)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var id = Guid.NewGuid();
        session.Store(new User { Id = id, Username = $"uniq-{Guid.NewGuid():n}"[..14], Email = email, PasswordHash = string.Empty });
        await session.SaveChangesAsync(Ct);
        return id;
    }

    private async Task<HttpClient> AdminOfAsync(string slug)
    {
        var userId = await UserAsync($"admin-{Guid.NewGuid():n}@example.com");
        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new Membership
            {
                Id = Guid.NewGuid(), UserId = userId, TenantSlug = slug, Status = MembershipStatus.Active,
                RoleIds = [SystemRoles.AdminRoleId],
            });
            await session.SaveChangesAsync(Ct);
        }

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(
                roles: ["Admin"],
                userId: userId.ToString(),
                additionalClaims: new Dictionary<string, string> { ["tenant"] = slug }));
        client.DefaultRequestHeaders.Add("X-Tenant", slug);
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, $"198.51.101.{Interlocked.Increment(ref _ipCounter) % 250 + 1}");
        return client;
    }
}
