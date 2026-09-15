using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Marten.Linq;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// Username and email uniqueness is enforced by the database on the value every lookup compares (#638).
/// </summary>
/// <remarks>
/// The unique indexes were on the value as entered and the lookups compared the lowercased one. So
/// <c>A@example.com</c> and <c>a@example.com</c> were two values to the index and one to the query:
/// two registrations racing past the query both inserted, and no lookup could use the index.
/// </remarks>
[Collection("Sequential")]
public class UserIdentityUniquenessTests
{
    private const string Password = "ValidPassword123!";

    private static int _ipCounter;

    private readonly IntegrationTestFixture _factory;
    private readonly HttpClient _client;

    public UserIdentityUniquenessTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Add(
            TestRemoteIpFilter.Header, $"2001:db8:638::{Interlocked.Increment(ref _ipCounter):x}");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string ConnectionString => _factory.ConnectionString + ";Include Error Detail=true";

    /// <summary>
    /// Both verifications run at once, so both pass the application's check before either inserts.
    /// Only the database can stop the second one, and only if it compares what the check compares.
    /// </summary>
    [Fact]
    public async Task Two_registrations_differing_only_in_case_verified_at_once_create_one_account()
    {
        var id = $"uq{Guid.NewGuid():N}"[..20];
        var upper = id.ToUpperInvariant();

        var first = await SeedPendingAsync(upper, $"{upper}@EXAMPLE.com");
        var second = await SeedPendingAsync(id, $"{id}@example.com");

        var responses = await Task.WhenAll(Verify(first), Verify(second));

        responses.Should().HaveCount(2);
        responses.Count(r => r.StatusCode == HttpStatusCode.OK).Should().Be(1,
            "one of the two has to win, and the other has to be refused rather than create a second account");
        responses.Should().OnlyContain(r => (int)r.StatusCode < 500, "a lost race is a refusal, not a crash");

        await using var session = _factory.Services.GetRequiredService<IDocumentStore>().QuerySession();
        var accounts = await session.Query<User>()
            .Where(u => u.Username == upper || u.Username == id)
            .ToListAsync(Ct);
        accounts.Should().HaveCount(1, "the two usernames and the two addresses differ only in case");
    }

    [Fact]
    public async Task The_database_refuses_a_second_account_whose_email_differs_only_in_case()
    {
        var id = $"uq{Guid.NewGuid():N}"[..20];
        var store = _factory.Services.GetRequiredService<IDocumentStore>();

        await using (var session = store.LightweightSession())
        {
            session.Store(new User { Id = Guid.NewGuid(), Username = $"{id}-a", Email = $"{id}@Example.com" });
            await session.SaveChangesAsync(Ct);
        }

        await using (var session = store.LightweightSession())
        {
            session.Store(new User { Id = Guid.NewGuid(), Username = $"{id}-b", Email = $" {id}@example.com" });
            var act = () => session.SaveChangesAsync(Ct);

            var thrown = await act.Should().ThrowAsync<Exception>(
                "no application check runs here, so the index is the only thing that can refuse it");
            IsUniqueViolation(thrown.Which).Should().BeTrue("it is refused as a duplicate: {0}", thrown.Which);
        }
    }

    [Fact]
    public async Task A_lookup_by_email_is_served_by_an_index()
    {
        await using var session = _factory.Services.GetRequiredService<IDocumentStore>().QuerySession();
        var plan = await PlanAsync(session.Query<User>().Where(u => u.NormalizedEmail == "someone@example.com"));

        plan.Should().Contain("mt_doc_users_uidx_normalized_email");
        plan.Should().NotContain("Seq Scan");
    }

    [Fact]
    public async Task A_lookup_by_username_is_served_by_an_index()
    {
        await using var session = _factory.Services.GetRequiredService<IDocumentStore>().QuerySession();
        var plan = await PlanAsync(session.Query<User>().Where(u => u.NormalizedUsername == "someone"));

        plan.Should().Contain("mt_doc_users_uidx_normalized_username");
        plan.Should().NotContain("Seq Scan");
    }

    [Fact]
    public async Task A_lookup_by_username_or_email_is_served_by_both_indexes()
    {
        await using var session = _factory.Services.GetRequiredService<IDocumentStore>().QuerySession();
        var plan = await PlanAsync(session.Query<User>()
            .Where(u => u.NormalizedUsername == "someone" || u.NormalizedEmail == "someone@example.com"));

        plan.Should().Contain("mt_doc_users_uidx_normalized_username");
        plan.Should().Contain("mt_doc_users_uidx_normalized_email");
        plan.Should().NotContain("Seq Scan");
    }

    /// <summary>
    /// Production runs AutoCreate.CreateOnly, so an existing database gets these indexes only from the
    /// hand-applied file, and the file has to build exactly what Marten would. A different name or
    /// expression makes every start-up schema check ask to drop and recreate it.
    /// </summary>
    [Fact]
    public async Task The_hand_applied_migration_creates_the_indexes_Marten_creates()
    {
        await using var session = _factory.Services.GetRequiredService<IDocumentStore>().QuerySession();
        var live = await session.AdvancedSql.QueryAsync<string>(
            "select indexdef from pg_indexes where schemaname = 'public' and tablename = 'mt_doc_users' "
          + "and indexdef like '%Normalized%'",
            Ct);

        live.Should().HaveCount(2, "Marten declares a unique index on each normalised field");

        var created = Regex.Matches(await MigrationAsync(),
                @"CREATE\s+UNIQUE\s+INDEX\s+IF\s+NOT\s+EXISTS\s+(\w+)\s+ON\s+public\.mt_doc_users\s+USING\s+btree\s*(\(.*?\));",
                RegexOptions.IgnoreCase | RegexOptions.Singleline)
            .ToDictionary(m => m.Groups[1].Value, m => Collapse(m.Groups[2].Value));
        created.Should().HaveCount(2);

        foreach (var indexdef in live)
        {
            var match = Regex.Match(indexdef, @"CREATE UNIQUE INDEX (\w+) ON public\.mt_doc_users USING btree (\(.*\))$");
            match.Success.Should().BeTrue("Marten builds a unique btree index: {0}", indexdef);

            created.Should().ContainKey(match.Groups[1].Value);
            created[match.Groups[1].Value].Should().Be(Collapse(match.Groups[2].Value));
        }
    }

    [Fact]
    public async Task The_migration_refuses_accounts_that_collide_once_normalized_and_names_them()
    {
        var id = $"mig{Guid.NewGuid():N}"[..20];
        var first = await StoreUserAsync(id, $"{id}-a@example.com");
        var second = await StoreUserAsync($"{id}-other", $"{id}-b@example.com");

        try
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync(Ct);
            await using var transaction = await connection.BeginTransactionAsync(Ct);

            // The shape of a database from before this change: raw indexes, no normalised fields, and
            // two accounts that differ only in case, which the raw index allowed.
            await ExecAsync(connection, transaction, PreNormalizationShape);
            await ExecAsync(connection, transaction,
                "update public.mt_doc_users set data = jsonb_set(data, '{Username}', to_jsonb(@name::text)) where id = @id",
                ("name", id.ToUpperInvariant()), ("id", second));

            var act = () => ExecAsync(connection, transaction, MigrationAsync().GetAwaiter().GetResult());

            var thrown = (await act.Should().ThrowAsync<PostgresException>()).Which;
            thrown.MessageText.Should().Contain("Nothing was changed");
            thrown.Detail.Should().Contain(first.ToString()).And.Contain(second.ToString(),
                "the operator has to be told which accounts to look at, not only that some exist");

            await transaction.RollbackAsync(Ct);
        }
        finally
        {
            await DeleteUsersAsync(first, second);
        }
    }

    [Fact]
    public async Task The_migration_backfills_existing_accounts_and_moves_the_indexes()
    {
        var id = $"mig{Guid.NewGuid():N}"[..20];
        var user = await StoreUserAsync(id.ToUpperInvariant(), $" {id.ToUpperInvariant()}@Example.com");

        try
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync(Ct);
            await using var transaction = await connection.BeginTransactionAsync(Ct);

            await ExecAsync(connection, transaction, PreNormalizationShape);

            var migration = await MigrationAsync();
            await ExecAsync(connection, transaction, migration);
            await ExecAsync(connection, transaction, migration); // and again, as an operator retrying would

            var normalized = await ScalarAsync(connection, transaction,
                "select (data ->> 'NormalizedUsername') || '|' || (data ->> 'NormalizedEmail') from public.mt_doc_users where id = @id",
                ("id", user));
            normalized.Should().Be($"{id}|{id}@example.com");

            var indexes = await ScalarAsync(connection, transaction,
                "select string_agg(indexname, ',' order by indexname) from pg_indexes "
              + "where tablename = 'mt_doc_users' and indexname like 'mt_doc_users_%idx_%'");
            indexes.Should().Be("mt_doc_users_uidx_normalized_email,mt_doc_users_uidx_normalized_username",
                "the raw indexes go, or they refuse nothing the normalised ones allow and cost every write");

            await transaction.RollbackAsync(Ct);
        }
        finally
        {
            await DeleteUsersAsync(user);
        }
    }

    /// <summary>
    /// A Development database never runs the migration file: CreateOrUpdate adds the indexes itself.
    /// An account stored before then has no normalised fields, and every lookup reads those.
    /// </summary>
    [Fact]
    public async Task Startup_writes_the_normalized_fields_an_older_account_is_missing()
    {
        var id = $"bf{Guid.NewGuid():N}"[..20];
        var user = await StoreUserAsync(id.ToUpperInvariant(), $"{id.ToUpperInvariant()}@Example.com");
        var store = _factory.Services.GetRequiredService<IDocumentStore>();

        try
        {
            await StripNormalizedAsync(user);

            await UserIdentityBackfill.RunAsync(store, Ct);

            await using var session = store.QuerySession();
            var found = await session.Query<User>().Where(u => u.NormalizedEmail == $"{id}@example.com").ToListAsync(Ct);
            found.Should().ContainSingle().Which.Id.Should().Be(user);
        }
        finally
        {
            await DeleteUsersAsync(user);
        }
    }

    [Fact]
    public async Task Startup_refuses_accounts_that_collide_once_normalized()
    {
        var id = $"bf{Guid.NewGuid():N}"[..20];
        var first = await StoreUserAsync(id, $"{id}-a@example.com");
        var second = await StoreUserAsync($"{id}-other", $"{id}-b@example.com");

        try
        {
            await StripNormalizedAsync(first);
            await StripNormalizedAsync(second, username: id.ToUpperInvariant());

            var act = () => UserIdentityBackfill.RunAsync(_factory.Services.GetRequiredService<IDocumentStore>(), Ct);

            (await act.Should().ThrowAsync<InvalidOperationException>())
                .WithMessage($"*{first}*").WithMessage($"*{second}*");
        }
        finally
        {
            await DeleteUsersAsync(first, second);
        }
    }

    /// <summary>
    /// The migration trims with btrim, which leaves a tab, so it stores "name" and "name" plus a tab as
    /// two values and its collision check passes them. .NET trims the tab, so sign-in sees one name.
    /// </summary>
    [Fact]
    public async Task Startup_refuses_accounts_the_migration_kept_apart_that_collide_once_trimmed_in_dotnet()
    {
        var id = $"bf{Guid.NewGuid():N}"[..20];
        var first = await StoreUserAsync(id, $"{id}-a@example.com");
        var second = await StoreUserAsync($"{id}-other", $"{id}-b@example.com");

        try
        {
            await SetUsernameAsMigratedAsync(first, id);
            await SetUsernameAsMigratedAsync(second, id + "\t");
            (await StoredNormalizedUsernameAsync(second)).Should().Be(id + "\t",
                "this is what the migration's lower(btrim(...)) stores, and the unique index allowed it");

            var act = () => UserIdentityBackfill.RunAsync(_factory.Services.GetRequiredService<IDocumentStore>(), Ct);

            (await act.Should().ThrowAsync<InvalidOperationException>())
                .WithMessage($"*{first}*").WithMessage($"*{second}*");
            (await StoredNormalizedUsernameAsync(second)).Should().Be(id + "\t", "a refusal changes nothing");
        }
        finally
        {
            await DeleteUsersAsync(first, second);
        }
    }

    [Fact]
    public async Task Startup_trims_a_tab_the_migration_left_so_the_account_can_sign_in_by_its_name()
    {
        var id = $"bf{Guid.NewGuid():N}"[..20];
        var user = await StoreUserAsync(id, $"{id}@example.com", BCrypt.Net.BCrypt.HashPassword(Password, 4));

        try
        {
            await SetUsernameAsMigratedAsync(user, id + "\t");

            await UserIdentityBackfill.RunAsync(_factory.Services.GetRequiredService<IDocumentStore>(), Ct);

            (await StoredNormalizedUsernameAsync(user)).Should().Be(id);
            var response = await _client.PostAsJsonAsync("/api/auth/login", new { Username = id, Password }, Ct);
            response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        }
        finally
        {
            await DeleteUsersAsync(user);
        }
    }

    /// <summary>
    /// lower() under lc_ctype C folds ASCII only, so the migration stores a non-ASCII capital as it is.
    /// The test database's locale may fold it, so the stored value is written as a C locale leaves it.
    /// </summary>
    [Fact]
    public async Task Startup_lowercases_a_non_ascii_letter_the_database_locale_left_alone()
    {
        var id = $"bf{Guid.NewGuid():N}"[..20];
        var user = await StoreUserAsync($"\u00C9{id}", $"{id}@example.com");

        try
        {
            await SetUsernameAsMigratedAsync(user, $"\u00C9{id}", storedNormalizedUsername: $"\u00C9{id}");

            await UserIdentityBackfill.RunAsync(_factory.Services.GetRequiredService<IDocumentStore>(), Ct);

            (await StoredNormalizedUsernameAsync(user)).Should().Be($"\u00E9{id}");
        }
        finally
        {
            await DeleteUsersAsync(user);
        }
    }

    private const string PreNormalizationShape = """
        drop index if exists public.mt_doc_users_uidx_normalized_username;
        drop index if exists public.mt_doc_users_uidx_normalized_email;
        create unique index mt_doc_users_uidx_username on public.mt_doc_users using btree ((data ->> 'Username'::text));
        create unique index mt_doc_users_uidx_email on public.mt_doc_users using btree ((data ->> 'Email'::text));
        update public.mt_doc_users set data = data - 'NormalizedUsername' - 'NormalizedEmail';
        """;

    private async Task<string> PlanAsync(IQueryable<User> query)
    {
        var command = query.ToCommand(FetchType.FetchMany);

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);

        // The fixture's table holds a few hundred rows, where a sequential scan is the cheaper plan
        // and the planner picks it whether or not an index could serve the query. Turning it off
        // leaves an index scan if, and only if, one applies.
        await ExecAsync(connection, transaction, "set local enable_seqscan = off");

        await using var explain = new NpgsqlCommand("explain " + command.CommandText, connection, transaction);
        foreach (NpgsqlParameter parameter in command.Parameters)
        {
            explain.Parameters.Add(parameter.Clone());
        }

        var lines = new List<string>();
        await using var reader = await explain.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            lines.Add(reader.GetString(0));
        }

        lines.Should().NotBeEmpty();
        return string.Join("\n", lines);
    }

    private async Task<Guid> StoreUserAsync(string username, string email, string passwordHash = "")
    {
        var id = Guid.NewGuid();
        await using var session = _factory.Services.GetRequiredService<IDocumentStore>().LightweightSession();
        session.Store(new User { Id = id, Username = username, Email = email, PasswordHash = passwordHash });
        await session.SaveChangesAsync(Ct);
        return id;
    }

    private async Task StripNormalizedAsync(Guid id, string? username = null)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(Ct);
        await ExecAsync(connection, null,
            "update public.mt_doc_users set data = jsonb_set(data - 'NormalizedUsername' - 'NormalizedEmail', "
          + "'{Username}', coalesce(to_jsonb(@name::text), data -> 'Username')) where id = @id",
            ("name", (object?)username ?? DBNull.Value), ("id", id));
    }

    /// <summary>
    /// Sets the username and stores the normalised username the way the 4.2.0 migration computes it,
    /// lower(btrim(...)), unless a stored value is given.
    /// </summary>
    private async Task SetUsernameAsMigratedAsync(Guid id, string username, string? storedNormalizedUsername = null)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(Ct);
        await ExecAsync(connection, null,
            "update public.mt_doc_users set data = data || jsonb_build_object('Username', @name::text, "
          + "'NormalizedUsername', coalesce(@stored::text, lower(btrim(@name::text)))) where id = @id",
            ("name", username), ("stored", (object?)storedNormalizedUsername ?? DBNull.Value), ("id", id));
    }

    private async Task<string?> StoredNormalizedUsernameAsync(Guid id)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "select data ->> 'NormalizedUsername' from public.mt_doc_users where id = @id", connection);
        command.Parameters.AddWithValue("id", id);
        return (await command.ExecuteScalarAsync(Ct))?.ToString();
    }

    private async Task DeleteUsersAsync(params Guid[] ids)
    {
        await using var session = _factory.Services.GetRequiredService<IDocumentStore>().LightweightSession();
        foreach (var id in ids)
        {
            session.Delete<User>(id);
        }

        await session.SaveChangesAsync(CancellationToken.None);
    }

    private static async Task ExecAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<string?> ScalarAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))?.ToString();
    }

    private async Task<string> SeedPendingAsync(string username, string email)
    {
        await using var session = _factory.Services.GetRequiredService<IDocumentStore>().LightweightSession();
        var pending = new PendingRegistration
        {
            Username = username,
            Email = email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(Password),
            ExpiresAt = DateTime.UtcNow.AddHours(1),
        };
        var (token, hash) = EmailVerificationToken.Create(pending.Id);
        pending.TokenHash = hash;
        session.Store(pending);
        await session.SaveChangesAsync(Ct);
        return token;
    }

    private Task<HttpResponseMessage> Verify(string token) =>
        _client.PostAsJsonAsync("/api/auth/register/verify", new { Token = token }, Ct);

    private static Task<string> MigrationAsync() =>
        File.ReadAllTextAsync(Path.Combine(RepositoryRoot(), "migrations", "4.2.0", "user-normalized-identity.sql"));

    private static string Collapse(string expression) => Regex.Replace(expression, @"\s+", " ").Trim();

    private static bool IsUniqueViolation(Exception? ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is PostgresException { SqlState: "23505" })
            {
                return true;
            }
        }

        return false;
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
