using System.Diagnostics;
using System.Globalization;
using System.Text;
using DotNet.Testcontainers.Containers;
using FluentAssertions;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// <c>scripts/db-app-role.sql</c> leaves a login role without superuser that owns what barakoCMS uses
/// in schema <c>public</c>, and refuses anything that would weaken the owner.
/// </summary>
/// <remarks>
/// The script uses psql meta-commands (<c>\if</c>, <c>\getenv</c>, <c>\ir</c>), which Npgsql does not
/// run, so each test runs the real file with <c>psql</c> inside the Postgres container. The container
/// is this class's own rather than the shared fixture's, because it needs server settings the other
/// tests should not carry: <c>pg_stat_statements</c>, <c>passwordcheck</c> and <c>auto_explain</c>
/// preloaded, and every statement logged three ways (statement, duration and plan), which is what makes "the password is not in the log" mean something. Every test
/// makes its own database and its own role names, since roles belong to the cluster.
/// <para>
/// Passwords are generated here and never written to the output of a failing assertion: the
/// assertions that look for one name what they looked for, not the text they searched.
/// </para>
/// </remarks>
[Collection("Integration1")]
public sealed class DbAppRoleScriptTests(DbAppRoleScriptTests.Postgres postgres)
    : IClassFixture<DbAppRoleScriptTests.Postgres>
{
    private const string ScriptDirectory = "/work/scripts";
    private const string WrapperDirectory = "/work/migrations/tenancy";
    private const string Script = ScriptDirectory + "/db-app-role.sql";
    private const string Wrapper = WrapperDirectory + "/001-app-role.sql";

    /// <summary>The message <c>passwordcheck</c> raises for a password that contains the role name.</summary>
    private const string PasswordCheckError = "password must not contain user name";

    private const string Objects = """
        CREATE TABLE public.notes (id serial PRIMARY KEY, body text);
        INSERT INTO public.notes (body) VALUES ('hello');
        CREATE TABLE public.events (at timestamptz NOT NULL) PARTITION BY RANGE (at);
        CREATE TABLE public.events_2026 PARTITION OF public.events
            FOR VALUES FROM ('2026-01-01') TO ('2027-01-01');
        CREATE SEQUENCE public.loose_numbers;
        CREATE VIEW public.note_bodies AS SELECT body FROM public.notes;
        CREATE MATERIALIZED VIEW public.note_count AS SELECT count(*) AS n FROM public.notes;
        CREATE FUNCTION public.shout(t text) RETURNS text LANGUAGE sql AS $$ SELECT upper(t) $$;
        CREATE PROCEDURE public.do_nothing() LANGUAGE sql AS $$ SELECT 1 $$;
        CREATE AGGREGATE public.count_rows(*) (sfunc = int8inc, stype = int8, initcond = '0');
        """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public sealed class Postgres : IAsyncLifetime
    {
        private readonly string _superuserPassword = "Su1-" + Guid.NewGuid().ToString("N");

        private readonly PostgreSqlContainer _container;

        public Postgres()
        {
            _container = new PostgreSqlBuilder("pgvector/pgvector:pg16")
                .WithUsername("postgres")
                .WithPassword(_superuserPassword)
                .WithCommand(
                    "-c", "shared_preload_libraries=pg_stat_statements,passwordcheck,auto_explain",
                    "-c", "log_statement=all",
                    "-c", "log_min_error_statement=error",
                    // Every statement is "slow", and auto_explain logs the text of every one, so a
                    // path other than log_statement that the script left open shows up in the log.
                    "-c", "log_min_duration_statement=0",
                    // Sampling logs what log_min_duration_statement = -1 leaves out, so each of the
                    // script's switches is the only thing standing between the password and the log.
                    "-c", "log_min_duration_sample=0",
                    "-c", "log_statement_sample_rate=1",
                    "-c", "log_transaction_sample_rate=1",
                    // The query tree of an ALTER ROLE carries the password string.
                    "-c", "debug_print_parse=on",
                    "-c", "debug_print_rewritten=on",
                    "-c", "debug_print_plan=on",
                    "-c", "auto_explain.log_min_duration=0")
                .Build();
        }

        public async ValueTask InitializeAsync()
        {
            await _container.StartAsync();
            await RunAsync("mkdir", "-p", ScriptDirectory, WrapperDirectory);
            await CopyFromRepositoryAsync("scripts/db-app-role.sql", Script);
            await CopyFromRepositoryAsync("migrations/tenancy/001-app-role.sql", Wrapper);
        }

        public async ValueTask DisposeAsync() => await _container.DisposeAsync();

        public string ConnectionString(string database, string? user = null, string? password = null)
        {
            var builder = new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
            {
                Host = "127.0.0.1",
                Database = database,
                Pooling = false,
            };
            builder.Username = user ?? builder.Username;
            builder.Password = user is null ? builder.Password : password;
            return builder.ConnectionString;
        }

        public Task<ExecResult> RunAsync(params string[] command) => _container.ExecAsync(command, Ct);

        /// <summary>
        /// Without timestamps. A timestamp starts every line, so it would land inside a wrapped tree
        /// dump and split the bytes <see cref="Leaks"/> looks for.
        /// </summary>
        public async Task<string> LogAsync()
        {
            var (stdout, stderr) = await _container.GetLogsAsync(timestampsEnabled: false, ct: Ct);
            return stdout + stderr;
        }

        private async Task CopyFromRepositoryAsync(string relative, string containerPath) =>
            await _container.CopyAsync(
                await File.ReadAllBytesAsync(Path.Combine(RepositoryRoot(), relative), Ct), containerPath, ct: Ct);
    }

    [Fact]
    public async Task A_fresh_database_ends_up_owned_by_a_role_without_superuser()
    {
        var (database, role, password) = (await NewDatabaseAsync(Objects, withExtension: true), NewRole(), NewPassword());

        var run = await RunScriptAsync(database, role, password);

        run.ExitCode.Should().Be(0, run.Stderr);
        run.Stdout.Should().Contain($"db-app-role: role {role}, rolsuper false, 9 objects changed owner");

        (await ScalarAsync(database, "SELECT rolsuper OR rolbypassrls FROM pg_roles WHERE rolname = @r", ("r", role)))
            .Should().Be(false);
        var objects = await ObjectsAsync(database);
        objects.Should().Contain(o => o.Kind == "schema" && o.Name == "public" && o.Owner == role);
        objects.Where(o => !o.Extension).Should().NotBeEmpty()
            .And.OnlyContain(o => o.Owner == role, "the role owns every object that is not an extension's");
        objects.Where(o => o.Extension).Should().Contain(o => o.Name == "pg_stat_statements")
            .And.OnlyContain(o => o.Owner == "postgres", "an extension's objects stay with the extension");

        await using var app = await OpenAsync(database, role, password);
        (await ScalarAsync(app, "SELECT body FROM public.notes")).Should().Be("hello");
        await ExecuteAsync(app, "ALTER TABLE public.notes ADD COLUMN extra integer");
        await ExecuteAsync(app, "CREATE TABLE public.created_by_the_role (id integer)");
    }

    [Fact]
    public async Task A_second_run_changes_no_owner_and_a_new_password_replaces_the_old()
    {
        var (database, role, first, second) = (await NewDatabaseAsync(Objects, true), NewRole(), NewPassword(), NewPassword());
        (await RunScriptAsync(database, role, first)).ExitCode.Should().Be(0);
        var before = await SnapshotAsync(database);

        var again = await RunScriptAsync(database, role, first);

        again.ExitCode.Should().Be(0, again.Stderr);
        again.Stdout.Should().Contain("0 objects changed owner");
        (await SnapshotAsync(database)).Should().Be(before, "no owner moved");

        (await RunScriptAsync(database, role, second)).ExitCode.Should().Be(0);

        var old = async () => await OpenAsync(database, role, first);
        (await old.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("28P01");
        await using var app = await OpenAsync(database, role, second);
        (await ScalarAsync(app, "SELECT 1")).Should().Be(1);
    }

    [Fact]
    public async Task An_existing_login_role_loses_the_extra_powers_it_had()
    {
        var (database, role, password) = (await NewDatabaseAsync(Objects, true), NewRole(), NewPassword());
        await ExecuteAdminAsync("postgres", $"CREATE ROLE {role} LOGIN CREATEDB CREATEROLE REPLICATION "
            + "VALID UNTIL '2000-01-01' CONNECTION LIMIT 0");

        var run = await RunScriptAsync(database, role, password);

        run.ExitCode.Should().Be(0, run.Stderr);
        (await ScalarAsync(database,
                "SELECT rolsuper OR rolcreatedb OR rolcreaterole OR rolreplication OR rolbypassrls "
                + "FROM pg_roles WHERE rolname = @r", ("r", role)))
            .Should().Be(false, "the script re-asserts every attribute, not only on a role it made");
        (await ScalarAsync(database,
                "SELECT rolconnlimit = -1 AND (rolvaliduntil IS NULL OR rolvaliduntil = 'infinity') "
                + "FROM pg_roles WHERE rolname = @r", ("r", role)))
            .Should().Be(true, "an expired or capped login would still not be able to connect");
        await using var app = await OpenAsync(database, role, password);
        (await ScalarAsync(app, "SELECT 1")).Should().Be(1);
    }

    [Fact]
    public async Task A_role_name_and_password_given_with_v_work_and_win_over_the_environment()
    {
        var (database, role, password) = (await NewDatabaseAsync(Objects, true), NewRole(), NewPassword());

        var run = await postgres.RunAsync(
            "env", "DB_APP_USER=ignored_in_favour_of_v", "DB_APP_PASSWORD=" + NewPassword(),
            "psql", "-X", "-q", "-U", "postgres", "-d", database,
            "-v", $"app_role={role}", "-v", $"app_password={password}", "-f", Script);

        run.ExitCode.Should().Be(0, run.Stderr);
        await using var app = await OpenAsync(database, role, password);
        (await ScalarAsync(app, "SELECT 1")).Should().Be(1);
    }

    [Fact]
    public async Task A_blank_role_name_becomes_barako_app()
    {
        var database = await NewDatabaseAsync(Objects, true);
        var password = NewPassword();

        var run = await RunScriptAsync(database, role: "", password);

        try
        {
            run.ExitCode.Should().Be(0, run.Stderr);
            run.Stdout.Should().Contain("db-app-role: role barako_app, rolsuper false");
            await using var app = await OpenAsync(database, "barako_app", password);
            (await ScalarAsync(app, "SELECT 1")).Should().Be(1);
        }
        finally
        {
            // The name is fixed, so it would collide with the next run on this container.
            await DropDatabaseAsync(database);
            await ExecuteAdminAsync("postgres", "DROP ROLE IF EXISTS barako_app");
        }
    }

    [Fact]
    public async Task The_old_path_includes_the_script_and_gives_the_same_result()
    {
        var (database, role, password) = (await NewDatabaseAsync(Objects, true), NewRole(), NewPassword());

        var run = await RunScriptAsync(database, role, password, path: Wrapper);

        run.ExitCode.Should().Be(0, run.Stderr);
        run.Stdout.Should().Contain($"db-app-role: role {role}, rolsuper false, 9 objects changed owner");
    }

    [Theory]
    [InlineData("owner", "role running this file")]
    [InlineData("other-superuser", "superuser or bypasses")]
    [InlineData("bypass-rls", "superuser or bypasses")]
    [InlineData("bad-name", "must match")]
    [InlineData("upper-case-name", "must match")]
    [InlineData("empty-password", "app_password is empty")]
    [InlineData("example-password", "placeholder or example")]
    [InlineData("docs-password", "placeholder or example")]
    [InlineData("example-password-with-newline", "placeholder or example")]
    [InlineData("whitespace-password", "app_password is empty")]
    [InlineData("reserved-name", "which Postgres reserves")]
    public async Task A_refusal_changes_nothing_and_the_owner_stays_a_superuser(string scenario, string message)
    {
        var database = await NewDatabaseAsync(Objects, withExtension: false);
        var other = NewRole();
        await ExecuteAdminAsync("postgres", $"CREATE ROLE {other} SUPERUSER");
        var bypass = NewRole();
        await ExecuteAdminAsync("postgres", $"CREATE ROLE {bypass} BYPASSRLS");
        var (role, password) = scenario switch
        {
            "owner" => ("postgres", NewPassword()),
            "other-superuser" => (other, NewPassword()),
            "bypass-rls" => (bypass, NewPassword()),
            "bad-name" => ("a;b", NewPassword()),
            "upper-case-name" => ("Barako_App", NewPassword()),
            "empty-password" => (NewRole(), ""),
            "example-password" => (NewRole(), "CHANGE-ME"),
            "docs-password" => (NewRole(), "change-me-to-a-strong-password"),
            "example-password-with-newline" => (NewRole(), "CHANGE-ME\n"),
            "whitespace-password" => (NewRole(), "  \n"),
            "reserved-name" => ("pg_monitor", NewPassword()),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
        var before = await SnapshotAsync(database);

        var run = await RunScriptAsync(database, role, password);

        run.ExitCode.Should().NotBe(0);
        run.Stderr.Should().Contain(message);
        run.Stdout.Should().NotContain("objects changed owner");
        (await SnapshotAsync(database)).Should().Be(before, "a refusal happens before anything changes");
        (await ScalarAsync(database, "SELECT rolsuper FROM pg_roles WHERE rolname = 'postgres'")).Should().Be(true);
    }

    [Fact]
    public async Task The_password_is_not_in_the_stats_view_after_a_run()
    {
        var (database, role, password) = (await NewDatabaseAsync(Objects, true), NewRole(), NewPassword());

        string tracked = "";
        var log = await LogDuringAsync(async () =>
        {
            (await RunScriptAsync(database, role, password)).ExitCode.Should().Be(0);

            // Read back and searched here: a password sent to the server as a parameter or in SQL text
            // would be logged by the server this test is checking (log_statement is all).
            tracked = (string)(await ScalarAsync(database,
                "SELECT coalesce(string_agg(query, E'\n'), '') FROM pg_stat_statements"))!;
        });

        tracked.Should().NotBeEmpty("the view is being read");
        Leaks(tracked, password).Should().BeFalse(
            "the password was looked for in the tracked statements; the script turns utility tracking off before it sets it");
        log.Should().Contain("duration:", "the window has to hold this server's log lines for the check to mean anything");
        Leaks(log, password).Should().BeFalse("the password was looked for in the server log during the run");
    }

    [Fact]
    public async Task The_password_is_not_in_the_log_of_a_successful_run_on_a_server_that_logs_every_duration()
    {
        var (database, role, password) = (await NewDatabaseAsync(Objects, true), NewRole(), NewPassword());

        ExecResult run = default;
        var log = await LogDuringAsync(async () => run = await RunScriptAsync(database, role, password));

        run.ExitCode.Should().Be(0, run.Stderr);
        // The statements outside the script's transaction and the end marker are what meet these two:
        // everything inside the transaction has logging turned off, which is what is being proved.
        log.Should().Contain("duration:", "this server logs every statement's duration, so its log is the one being checked");
        log.Should().Contain("Query Text:", "auto_explain is logging the text of the statements it can see");
        Leaks(log, password).Should().BeFalse("the password was looked for in the server log, as text and as the bytes of a parse tree constant");
    }

    [Fact]
    public async Task A_function_the_app_role_planted_in_public_is_not_called_as_the_superuser()
    {
        var (database, role, password) = (await NewDatabaseAsync(Objects, true), NewRole(), NewPassword());
        (await RunScriptAsync(database, role, password)).ExitCode.Should().Be(0);
        await using (var app = await OpenAsync(database, role, password))
        {
            // Same name as the catalog function the script calls with an integer, and an exact match
            // for it, so with public on the search_path this would win and run as the superuser.
            await ExecuteAsync(app, """
                CREATE TABLE public.hijack_log (called_as text, was_super boolean);
                CREATE FUNCTION public.hashtextextended(t text, s integer) RETURNS bigint LANGUAGE plpgsql AS $f$
                BEGIN
                    INSERT INTO public.hijack_log
                    VALUES (current_user, (SELECT rolsuper FROM pg_roles WHERE rolname = current_user));
                    RETURN 0;
                END
                $f$
                """);
        }

        var again = await RunScriptAsync(database, role, password);

        again.ExitCode.Should().Be(0, again.Stderr);
        (await ScalarAsync(database, "SELECT count(*) FROM public.hijack_log")).Should().Be(0L, "the planted function was never called");
        (await ScalarAsync(database, "SELECT rolsuper FROM pg_roles WHERE rolname = @r", ("r", role))).Should().Be(false);
        (await ScalarAsync(database,
                "SELECT count(*) FROM pg_auth_members m JOIN pg_roles g ON g.oid = m.roleid "
                + "WHERE g.rolsuper AND m.member = (SELECT oid FROM pg_roles WHERE rolname = @r)", ("r", role)))
            .Should().Be(0L, "the role is in no superuser role");
    }

    [Fact]
    public async Task The_password_is_not_in_the_log_of_a_run_that_fails_at_the_password_step()
    {
        var (database, role) = (await NewDatabaseAsync(Objects, true), NewRole());
        // passwordcheck refuses a password that contains the role name, and only when the password
        // is set, which is the step that has to fail. Everything before it is accepted.
        var password = "Aa1-" + role + "-" + Guid.NewGuid().ToString("N");

        ExecResult run = default;
        var log = await LogDuringAsync(async () => run = await RunScriptAsync(database, role, password));

        run.ExitCode.Should().NotBe(0);
        run.Stderr.Should().Contain(PasswordCheckError, "the run has to fail at the password step for this test to mean anything");
        run.Stderr.Contains(password).Should().BeFalse("the password was looked for in what psql printed");
        log.Should().Contain(PasswordCheckError, "the server logged the failure, so its log is the one being checked");
        Leaks(log, password).Should().BeFalse("the password was looked for in the server log, beside the error");
        (await ScalarAsync(database, "SELECT count(*) FROM pg_roles WHERE rolname = @r", ("r", role)))
            .Should().Be(0L, "the failed run rolled back");
    }

    [Fact]
    public async Task A_run_waits_while_another_holds_the_lock_and_then_succeeds()
    {
        var (database, role, password) = (await NewDatabaseAsync(Objects, true), NewRole(), NewPassword());
        await using var holder = await OpenAdminAsync(database);
        await using var held = await holder.BeginTransactionAsync(Ct);
        await using (var lockCommand = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended('barakoCMS.db-app-role', 0))", holder, held))
        {
            await lockCommand.ExecuteNonQueryAsync(Ct);
        }

        var run = RunScriptAsync(database, role, password);

        await WaitUntilAsync(async () => (long)(await ScalarAsync(database,
            "SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND NOT granted "
            + "AND database = (SELECT oid FROM pg_database WHERE datname = current_database())"))! == 1);
        run.IsCompleted.Should().BeFalse("the run is waiting for the lock");
        (await ScalarAsync(database, "SELECT count(*) FROM pg_roles WHERE rolname = @r", ("r", role)))
            .Should().Be(0L, "nothing changes before the lock is taken");

        await held.CommitAsync(Ct);
        var result = await run;

        result.ExitCode.Should().Be(0, result.Stderr);
        result.Stdout.Should().Contain($"db-app-role: role {role}, rolsuper false");
    }

    [Fact]
    public async Task Two_runs_started_together_both_succeed()
    {
        var (database, role, password) = (await NewDatabaseAsync(Objects, true), NewRole(), NewPassword());

        var runs = await Task.WhenAll(
            RunScriptAsync(database, role, password), RunScriptAsync(database, role, password));

        runs.Should().OnlyContain(r => r.ExitCode == 0, string.Join(" | ", runs.Select(r => r.Stderr)));
        runs.Count(r => r.Stdout.Contains("9 objects changed owner")).Should().Be(1, "one run moved them");
        runs.Count(r => r.Stdout.Contains(", 0 objects changed owner")).Should().Be(1, "the other found nothing to move");
        await using var app = await OpenAsync(database, role, password);
        (await ScalarAsync(app, "SELECT 1")).Should().Be(1);
    }

    [Fact]
    public void The_quickstart_copy_is_the_same_file()
    {
        var root = RepositoryRoot();

        File.ReadAllBytes(Path.Combine(root, "quickstart/scripts/db-app-role.sql"))
            .Should().Equal(File.ReadAllBytes(Path.Combine(root, "scripts/db-app-role.sql")),
                "the quickstart folder is copied out of the repository, so it carries its own copy");
    }

    [Theory]
    [InlineData("same", 0, "matches")]
    [InlineData("differs", 1, "quickstart/scripts/db-app-role.sql differs from scripts/db-app-role.sql")]
    [InlineData("missing", 1, "quickstart/scripts/db-app-role.sql is missing")]
    public async Task The_copy_check_fails_when_the_quickstart_copy_differs_or_is_missing(
        string state, int exitCode, string message)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the check is a bash script");
        var root = RepositoryRoot();
        var temp = Directory.CreateTempSubdirectory("db-app-role-copy-check-");
        try
        {
            foreach (var file in new[]
            {
                "scripts/check-quickstart-backup-script.sh", "scripts/backup-cron.sh", "scripts/db-app-role.sql",
                "quickstart/scripts/backup-cron.sh", "quickstart/scripts/db-app-role.sql",
            })
            {
                var target = Path.Combine(temp.FullName, file);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(Path.Combine(root, file), target);
            }

            var copy = Path.Combine(temp.FullName, "quickstart/scripts/db-app-role.sql");
            if (state == "differs") File.AppendAllText(copy, "SELECT 1;\n");
            if (state == "missing") File.Delete(copy);

            var (code, output) = await RunBashAsync(temp.FullName, "scripts/check-quickstart-backup-script.sh");

            code.Should().Be(exitCode, output);
            output.Should().Contain(message);
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    private static async Task<(int Code, string Output)> RunBashAsync(string directory, string script)
    {
        var start = new ProcessStartInfo("bash")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(script);

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(Ct);
        var error = process.StandardError.ReadToEndAsync(Ct);
        await process.WaitForExitAsync(Ct);
        return (process.ExitCode, await output + await error);
    }

    private static string NewRole() => "app_" + Guid.NewGuid().ToString("N")[..16];

    /// <summary>Long, with a letter, a digit and a symbol, so <c>passwordcheck</c> accepts it.</summary>
    private static string NewPassword() => "Aa1-" + Guid.NewGuid().ToString("N");

    private static string RepositoryRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "barakoCMS.sln")))
        {
            root = root.Parent;
        }

        root.Should().NotBeNull("the repository root is above the test output");
        return root!.FullName;
    }

    /// <summary>
    /// A database with the objects the script has to take over, made by the owner. The extension is
    /// left out of the databases a refusal runs against, so that a refusal installing it shows.
    /// </summary>
    private async Task<string> NewDatabaseAsync(string sql, bool withExtension)
    {
        var database = "db_" + Guid.NewGuid().ToString("N")[..16];
        await ExecuteAdminAsync("postgres", $"CREATE DATABASE {database}");
        await ExecuteAdminAsync(database, sql);
        if (withExtension)
        {
            await ExecuteAdminAsync(database, "CREATE EXTENSION pg_stat_statements");
        }

        return database;
    }

    private Task DropDatabaseAsync(string database) =>
        ExecuteAdminAsync("postgres", $"DROP DATABASE {database} WITH (FORCE)");

    /// <summary>
    /// Runs the file as the superuser with psql. The role and password go in through the environment,
    /// the way the stack hands them over, unless the role is null, which leaves the variable unset.
    /// </summary>
    private async Task<ExecResult> RunScriptAsync(string database, string? role, string password, string path = Script)
    {
        var command = new List<string> { "env", "DB_APP_PASSWORD=" + password };
        if (role is not null)
        {
            command.Add("DB_APP_USER=" + role);
        }

        command.AddRange(["psql", "-X", "-q", "-U", "postgres", "-d", database, "-f", path]);
        return await postgres.RunAsync([.. command]);
    }

    private sealed record DbObject(string Kind, string Name, string Owner, bool Extension);

    private async Task<List<DbObject>> ObjectsAsync(string database)
    {
        const string sql = """
            SELECT 'relation', c.relname::text, pg_get_userbyid(c.relowner)::text,
                   EXISTS (SELECT 1 FROM pg_depend d
                           WHERE d.classid = 'pg_class'::regclass AND d.objid = c.oid AND d.deptype = 'e')
            FROM pg_class c
            WHERE c.relnamespace = 'public'::regnamespace AND c.relkind IN ('r', 'p', 'v', 'm', 'S', 'f')
            UNION ALL
            SELECT 'function', p.proname::text, pg_get_userbyid(p.proowner)::text,
                   EXISTS (SELECT 1 FROM pg_depend d
                           WHERE d.classid = 'pg_proc'::regclass AND d.objid = p.oid AND d.deptype = 'e')
            FROM pg_proc p WHERE p.pronamespace = 'public'::regnamespace
            UNION ALL
            SELECT 'schema', 'public', pg_get_userbyid(nspowner)::text, false
            FROM pg_namespace WHERE nspname = 'public'
            UNION ALL
            SELECT 'extension', extname::text, pg_get_userbyid(extowner)::text, true FROM pg_extension
            ORDER BY 1, 2
            """;
        await using var connection = await OpenAdminAsync(database);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var found = new List<DbObject>();
        while (await reader.ReadAsync(Ct))
        {
            found.Add(new DbObject(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3)));
        }

        // The extension row is a marker for "installed"; what belongs to it is the relations above.
        return found;
    }

    /// <summary>What a refusal must leave alone: every object and owner, the roles, and the extensions.</summary>
    private async Task<string> SnapshotAsync(string database)
    {
        var objects = await ObjectsAsync(database);
        var roles = (string)(await ScalarAsync(database,
            "SELECT string_agg(rolname || ':' || rolsuper || ':' || rolbypassrls || ':' || rolcanlogin, ',' ORDER BY rolname) "
            + "FROM pg_roles WHERE rolname !~ '^pg_'"))!;
        return string.Join(";", objects.Select(o => $"{o.Kind}/{o.Name}/{o.Owner}")) + "|" + roles;
    }

    private async Task<NpgsqlConnection> OpenAdminAsync(string database)
    {
        var connection = new NpgsqlConnection(postgres.ConnectionString(database));
        await connection.OpenAsync(Ct);
        return connection;
    }

    private async Task<NpgsqlConnection> OpenAsync(string database, string role, string password)
    {
        var connection = new NpgsqlConnection(postgres.ConnectionString(database, role, password));
        await connection.OpenAsync(Ct);
        return connection;
    }

    private async Task ExecuteAdminAsync(string database, string sql)
    {
        await using var connection = await OpenAdminAsync(database);
        await ExecuteAsync(connection, sql);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync(Ct);
    }

    private async Task<object?> ScalarAsync(string database, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = await OpenAdminAsync(database);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return await command.ExecuteScalarAsync(Ct);
    }

    /// <summary>
    /// Runs <paramref name="act"/> between two marker statements and returns the server log between
    /// them. The container's log is shared by the whole class, so only this slice says anything about
    /// this run. A backend writes its log line before the client gets the result, so once the end
    /// marker shows, the lines of the run are in.
    /// </summary>
    private async Task<string> LogDuringAsync(Func<Task> act)
    {
        var start = await MarkAsync("start");
        await act();
        var end = await MarkAsync("end");

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            var log = await postgres.LogAsync();
            var from = log.IndexOf(start, StringComparison.Ordinal);
            var to = from < 0 ? -1 : log.IndexOf(end, from, StringComparison.Ordinal);
            if (to >= 0)
            {
                return log[from..to];
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The end marker never showed in the server log.");
            }

            await Task.Delay(250, Ct);
        }
    }

    /// <summary>A marker is not secret, so it goes in as SQL text.</summary>
    private async Task<string> MarkAsync(string name)
    {
        var marker = $"db-app-role-{name}-{Guid.NewGuid():N}";
        var result = await postgres.RunAsync("psql", "-X", "-q", "-U", "postgres", "-d", "postgres", "-c", $"SELECT '{marker}'");
        result.ExitCode.Should().Be(0, result.Stderr);
        return marker;
    }

    /// <summary>
    /// Whether the password is in the text, as itself or as the bytes of a text constant (the server
    /// prints a parse tree constant as signed decimals) with all whitespace removed, because the
    /// server wraps those dumps at 78 characters, even in the middle of a number or a string.
    /// </summary>
    private static bool Leaks(string text, string password)
    {
        var squeezed = new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray());
        var bytes = string.Concat(Encoding.UTF8.GetBytes(password)
            .Select(b => ((sbyte)b).ToString(CultureInfo.InvariantCulture)));
        return squeezed.Contains(password, StringComparison.Ordinal) || squeezed.Contains(bytes, StringComparison.Ordinal);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The run never started waiting for the advisory lock.");
            }

            await Task.Delay(100, Ct);
        }
    }
}
