using System.Security.Cryptography;
using System.Text.RegularExpressions;
using DotNet.Testcontainers.Containers;
using FluentAssertions;
using Testcontainers.PostgreSql;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// Runs the two SQL blocks under "The app role on a managed database" in
/// docs/deploy-on-a-managed-platform.md as an admin that has CREATEROLE and CREATEDB and is not a
/// superuser, which is what a managed platform gives. The doc and this test cannot drift apart.
/// </summary>
/// <remarks>
/// The container is this class's own because the steps create a cluster-wide role. The role's
/// password is random, is set only through the file the test writes, and is never printed or
/// asserted in a message.
/// </remarks>
[Collection("Integration1")]
public sealed class ManagedAdminRoleStepsTests(ManagedAdminRoleStepsTests.Postgres postgres)
    : IClassFixture<ManagedAdminRoleStepsTests.Postgres>
{
    private const string Heading = "### The app role on a managed database";
    private const string Database = "managed_steps_db";
    private const string Seed = """
        CREATE TABLE public.notes (id serial PRIMARY KEY, body text);
        INSERT INTO public.notes (body) VALUES ('hello');
        CREATE SEQUENCE public.loose_numbers;
        CREATE VIEW public.note_bodies AS SELECT body FROM public.notes;
        CREATE FUNCTION public.shout(t text) RETURNS text LANGUAGE sql AS $$ SELECT upper(t) $$;
        """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public sealed class Postgres : IAsyncLifetime
    {
        private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("pgvector/pgvector:pg16")
            .WithUsername("postgres")
            .WithPassword("Su1-" + Guid.NewGuid().ToString("N"))
            .Build();

        public async ValueTask InitializeAsync()
        {
            await _container.StartAsync(Ct);
            await RunAsync("mkdir", "-p", "/work/steps", "/work/scripts");
            await _container.CopyAsync(
                await File.ReadAllBytesAsync(Path.Combine(RepositoryRoot(), "scripts/db-app-role.sql"), Ct),
                "/work/scripts/db-app-role.sql", ct: Ct);
        }

        public async ValueTask DisposeAsync() => await _container.DisposeAsync();

        public Task<ExecResult> RunAsync(params string[] command) => _container.ExecAsync(command, Ct);

        public Task WriteAsync(string path, string text) =>
            _container.CopyAsync(System.Text.Encoding.UTF8.GetBytes(text), path, ct: Ct);

        public Task<ExecResult> PsqlAsync(string user, string database, params string[] args) =>
            RunAsync(["psql", "-X", "-v", "ON_ERROR_STOP=1", "-At", "-U", user, "-d", database, .. args]);
    }

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

    private static string[] DocBlocks()
    {
        var doc = File.ReadAllText(Path.Combine(RepositoryRoot(), "docs/deploy-on-a-managed-platform.md"));
        var start = doc.IndexOf(Heading, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, "the doc has the section");
        var end = doc.IndexOf("\n## ", start, StringComparison.Ordinal);
        var section = doc[start..(end < 0 ? doc.Length : end)];

        var blocks = Regex.Matches(section, @"```sql\r?\n(.*?)```", RegexOptions.Singleline)
            .Select(m => m.Groups[1].Value).ToArray();
        blocks.Should().HaveCount(2, "the section holds the role block and the ownership block, and nothing else in sql");
        return blocks;
    }

    private async Task<string> QueryAsync(string sql)
    {
        var run = await postgres.PsqlAsync("postgres", Database, "-c", sql);
        run.ExitCode.Should().Be(0, run.Stderr);
        return run.Stdout.Trim();
    }

    [Fact]
    public async Task The_steps_in_the_doc_give_a_role_without_superuser_everything_the_API_uses()
    {
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var blocks = DocBlocks();
        blocks[0].Should().Contain("\\password barako_app");
        var roleBlock = Regex.Replace(
            blocks[0], @"^\\password barako_app\s*$", $"ALTER ROLE barako_app PASSWORD '{password}';", RegexOptions.Multiline);
        roleBlock.Should().NotContain("\\password", "the line was replaced");

        await postgres.WriteAsync("/work/steps/role.sql", roleBlock);
        await postgres.WriteAsync("/work/steps/move.sql", blocks[1]);

        var admin = await postgres.RunAsync(
            "psql", "-X", "-v", "ON_ERROR_STOP=1", "-U", "postgres", "-d", "postgres", "-c",
            $"CREATE ROLE mgr LOGIN CREATEROLE CREATEDB NOSUPERUSER; CREATE DATABASE {Database} OWNER mgr");
        admin.ExitCode.Should().Be(0, admin.Stderr);
        var extension = await postgres.PsqlAsync("postgres", Database, "-c", "CREATE EXTENSION pg_stat_statements");
        extension.ExitCode.Should().Be(0, extension.Stderr);
        var seed = await postgres.PsqlAsync("mgr", Database, "-c", Seed);
        seed.ExitCode.Should().Be(0, seed.Stderr);
        (await QueryAsync("select rolsuper from pg_roles where rolname = 'mgr'")).Should().Be("f");

        var first = await postgres.PsqlAsync("mgr", Database, "-f", "/work/steps/role.sql");
        first.ExitCode.Should().Be(0, first.Stderr);
        var move = await postgres.PsqlAsync("mgr", Database, "-f", "/work/steps/move.sql");
        move.ExitCode.Should().Be(0, move.Stderr);

        (await QueryAsync(
            "select pg_get_userbyid(nspowner) from pg_namespace where nspname = 'public'")).Should().Be("barako_app");

        var notMoved = await QueryAsync("""
            select string_agg(kind || ' ' || name, ', ') from (
              select 'relation' as kind, c.oid::regclass::text as name
                from pg_class c
               where c.relnamespace = 'public'::regnamespace and c.relkind in ('r','p','v','m','S','f')
                 and pg_get_userbyid(c.relowner) <> 'barako_app'
                 and not exists (select 1 from pg_depend d where d.classid = 'pg_class'::regclass
                                  and d.objid = c.oid and d.deptype = 'e')
              union all
              select 'routine', p.oid::regprocedure::text
                from pg_proc p
               where p.pronamespace = 'public'::regnamespace and pg_get_userbyid(p.proowner) <> 'barako_app'
                 and not exists (select 1 from pg_depend d where d.classid = 'pg_proc'::regclass
                                  and d.objid = p.oid and d.deptype = 'e')
            ) x
            """);
        notMoved.Should().BeEmpty("every relation and routine outside an extension moved to the app role");

        (await QueryAsync("""
            select count(*) from pg_class c
             where c.relnamespace = 'public'::regnamespace and c.relkind in ('r','v','S')
               and pg_get_userbyid(c.relowner) = 'barako_app'
            """)).Should().NotBe("0", "the table, its sequence, the loose sequence and the view moved");

        var extensionOwners = await QueryAsync("""
            select count(*) from pg_proc p join pg_depend d
              on d.classid = 'pg_proc'::regclass and d.objid = p.oid and d.deptype = 'e'
             where p.pronamespace = 'public'::regnamespace and pg_get_userbyid(p.proowner) = 'postgres'
            """);
        extensionOwners.Should().NotBe("0", "the extension's members kept their owner");
        (await QueryAsync("select pg_get_userbyid(relowner) from pg_class where oid = 'public.pg_stat_statements'::regclass"))
            .Should().Be("postgres");

        var again = await postgres.PsqlAsync("mgr", Database, "-f", "/work/steps/move.sql");
        again.ExitCode.Should().Be(0, again.Stderr);

        var app = await postgres.PsqlAsync(
            "barako_app", Database, "-c",
            "select public.shout('x'); insert into public.notes (body) values ('two'); create table public.made_by_app (id int)");
        app.ExitCode.Should().Be(0, app.Stderr);
    }

    [Fact]
    public async Task The_role_script_still_needs_a_superuser_so_the_doc_sends_a_managed_admin_to_the_steps()
    {
        var admin = await postgres.RunAsync(
            "psql", "-X", "-U", "postgres", "-d", "postgres", "-c",
            "DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'mgr2') THEN CREATE ROLE mgr2 LOGIN CREATEROLE CREATEDB; END IF; END $$");
        admin.ExitCode.Should().Be(0, admin.Stderr);
        var created = await postgres.RunAsync(
            "psql", "-X", "-U", "postgres", "-d", "postgres", "-c", "CREATE DATABASE managed_script_db OWNER mgr2");
        created.ExitCode.Should().Be(0, created.Stderr);

        var run = await postgres.RunAsync(
            "env", "DB_APP_USER=barako_script_role", "DB_APP_PASSWORD=Aa1-" + Guid.NewGuid().ToString("N"),
            "psql", "-X", "-q", "-v", "ON_ERROR_STOP=1", "-U", "mgr2", "-d", "managed_script_db",
            "-f", "/work/scripts/db-app-role.sql");

        run.ExitCode.Should().NotBe(0, "scripts/db-app-role.sql needs a superuser; if this starts passing, update the doc");
    }
}
