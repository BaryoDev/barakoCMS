using System.Diagnostics;
using barakoCMS.Infrastructure.Migrations;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// The ledger as a real host uses it: the command on the host the published image runs, the rows a
/// first start writes, and the skip queries of the files released before the ledger (issue #901).
/// </summary>
[Collection("Integration1")]
public class MigrationHostTests
{
    private readonly IntegrationTestFixture _factory;

    public MigrationHostTests(IntegrationTestFixture factory) => _factory = factory;

    private static readonly string CoreAssembly = typeof(barakoCMS.Extensions.ServiceCollectionExtensions).Assembly.Location;

    /// <summary>
    /// Both hosts in this repository, each started as its own process with <c>db-migrate</c> as its
    /// argument. The Suite is the host <c>ghcr.io/baryodev/barako-cms</c> runs, and it loads every
    /// module, so it is where a module's migration has to show up under the module's name. The core
    /// host loads none, so it must record core's files and nothing else.
    /// </summary>
    [Theory]
    [InlineData("suite")]
    [InlineData("core")]
    public async Task A_host_answers_db_migrate_and_a_second_run_changes_nothing(string host)
    {
        var assembly = host == "suite" ? SuiteAssemblyPath() : CoreAssembly;
        File.Exists(assembly).Should().BeTrue($"the {host} host must be built at {assembly}");
        await using var database = await MigrationScratchDatabase.CreateAsync(_factory, started: false);

        var first = await RunHostAsync(assembly, database.ConnectionString, "db-migrate");
        var ledgerAfterFirst = await database.LedgerAsync();
        var second = await RunHostAsync(assembly, database.ConnectionString, "db-migrate");
        var status = await RunHostAsync(assembly, database.ConnectionString, "db-migrate", "--status");

        first.ExitCode.Should().Be(0, $"output:\n{Truncate(first.Output)}");
        ledgerAfterFirst.Count.Should().BeGreaterThanOrEqualTo(host == "suite" ? 8 : 7,
            "seven core files and one module file were released before the ledger");
        ledgerAfterFirst.Should().Contain("core/4.0.0/3.x-to-4.0 baselined");
        ledgerAfterFirst.Should().OnlyContain(row => row.EndsWith(" baselined"),
            "nothing has started against this database, so no file is run");
        if (host == "suite")
        {
            ledgerAfterFirst.Should().Contain("Files/4.2.0/stored-files-parent-index baselined");
        }
        else
        {
            ledgerAfterFirst.Should().OnlyContain(row => row.StartsWith("core/"),
                "a module that is not loaded contributes no migration");
        }

        second.ExitCode.Should().Be(0, $"output:\n{Truncate(second.Output)}");
        second.Output.Should().Contain("0 applied, 0 baselined");
        (await database.LedgerAsync()).Should().Equal(ledgerAfterFirst);
        status.ExitCode.Should().Be(0, $"output:\n{Truncate(status.Output)}");
        (await database.IsTrueAsync("select to_regclass('public.mt_doc_users') is null"))
            .Should().BeTrue("the command records migrations and does not apply Marten's schema");
    }

    /// <summary>
    /// <c>db-apply</c> is the other way a schema gets created on an empty database. If it left no
    /// rows, the database would look like one migrated by hand, and the first <c>db-migrate</c> would
    /// run every file that has no skip query.
    /// </summary>
    [Fact]
    public async Task db_apply_on_a_new_database_records_the_migrations_it_makes_unnecessary()
    {
        await using var database = await MigrationScratchDatabase.CreateAsync(_factory, started: false);

        var apply = await RunHostAsync(CoreHostAssemblyPath(), database.ConnectionString, "db-apply");
        var ledger = await database.LedgerAsync();

        apply.ExitCode.Should().Be(0, $"output:\n{Truncate(apply.Output)}");
        (await database.IsTrueAsync("select to_regclass('public.mt_doc_users') is not null"))
            .Should().BeTrue("db-apply creates the schema");
        ledger.Count.Should().BeGreaterThanOrEqualTo(7);
        ledger.Should().OnlyContain(row => row.StartsWith("core/") && row.EndsWith(" baselined"));
    }

    /// <summary>
    /// The fixture's own start was the first against its database, in this process, through
    /// <c>Program.cs</c>. Without the rows it writes, the first <c>db-migrate</c> on an install that
    /// began on this release would run every file that has no skip query against a schema that is
    /// already current.
    /// </summary>
    [Fact]
    public async Task The_first_start_on_a_new_database_records_cores_migrations_as_baselined()
    {
        var ct = TestContext.Current.CancellationToken;
        var core = ShippedMigrations.FromAssembly(ShippedMigrations.CoreOwner, typeof(ShippedMigrations).Assembly);
        var rows = new Dictionary<string, string>();

        await using (var connection = new NpgsqlConnection(_factory.ConnectionString))
        {
            await connection.OpenAsync(ct);
            await using var command = new NpgsqlCommand(
                "select id, state || ' ' || checksum from public.barako_migrations where owner = 'core'", connection);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                rows[reader.GetString(0)] = reader.GetString(1);
        }

        core.Count.Should().BeGreaterThanOrEqualTo(7);
        foreach (var migration in core)
        {
            rows.Should().ContainKey(migration.Id);
            rows[migration.Id].Should().Be($"{MigrationState.Baselined} {migration.Checksum}");
        }
    }

    /// <summary>
    /// An existing deployment applied these files by hand and has no ledger. Its first run decides
    /// what to leave alone by each file's skip query, so every one of them has to be true on a
    /// database that already has the schema. The fixture's database has it.
    /// </summary>
    [Fact]
    public async Task Every_pre_ledger_file_is_skipped_on_a_database_that_already_has_its_change()
    {
        var ct = TestContext.Current.CancellationToken;
        using (var scope = _factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
            foreach (var type in new[]
                     {
                         typeof(barakoCMS.Models.User),
                         typeof(barakoCMS.Models.RefreshToken),
                         typeof(barakoCMS.Models.SiteShareLink),
                         typeof(barakoCMS.Models.CollectionSync),
                     })
            {
                await store.Storage.Database.EnsureStorageExistsAsync(type, ct);
            }

            await store.Storage.Database.EnsureStorageExistsAsync(typeof(BarakoCMS.Files.StoredFile), ct);
        }

        var preLedger = ShippedMigrations
            .Discover([new BarakoCMS.Files.FilesModule()])
            .Where(m => Version.Parse(m.Version) < new Version(4, 6, 0))
            .ToList();
        // Eight, not nine: forms-public-forms (4.2.0) was pre-ledger and left with BarakoCMS.Forms in 5.5.0.
        preLedger.Should().HaveCount(8);

        await using var connection = new NpgsqlConnection(_factory.ConnectionString);
        await connection.OpenAsync(ct);
        foreach (var migration in preLedger)
        {
            migration.SkipWhen.Should().NotBeNull($"{migration.Key} was released before the ledger");
            await using var command = new NpgsqlCommand(migration.SkipWhen, connection);
            (await command.ExecuteScalarAsync(ct)).Should().Be(true,
                $"{migration.Key} is already in place here, and running it again is what the query is there to prevent");
        }
    }

    /// <summary>
    /// A 5.4 database with every business module enabled, started on 5.5.0. The modules are gone,
    /// so their migration files are not shipped and their ledger rows are for files this build does
    /// not have. db-migrate has to report that and carry on, and it must not touch the tables.
    /// </summary>
    [Theory]
    [InlineData("suite")]
    [InlineData("core")]
    public async Task A_database_that_ran_the_removed_modules_migrates_without_errors_and_keeps_their_tables(string host)
    {
        var assembly = host == "suite" ? SuiteAssemblyPath() : CoreAssembly;
        File.Exists(assembly).Should().BeTrue($"the {host} host must be built at {assembly}");
        // A real 5.4 database has a row for every core file next to the module rows, written by its
        // first start. Without them, db-migrate would run the core files against a schema this test
        // never built. db-migrate on the empty database writes the same rows, as baselined.
        await using var database = await MigrationScratchDatabase.CreateAsync(_factory, started: false);
        var first = await RunHostAsync(assembly, database.ConnectionString, "db-migrate");
        first.ExitCode.Should().Be(0, $"output:\n{Truncate(first.Output)}");
        // Started since, so the run below cannot take the new-database path.
        await database.ExecuteAsync("create table public.mt_doc_users (id uuid primary key)");

        var removed = Path.Combine(RepositoryRoot(), "migrations", "removed");
        var owners = new Dictionary<string, string>
        {
            ["Forms"] = "4.2.0/forms-public-forms",
            ["Booking"] = "5.2.0/booking-resources",
            ["Payments"] = "5.2.0/payments-attempts",
            ["Reports"] = "5.2.0/report-definitions",
            ["Scheduling"] = "5.2.0/scheduled-entries",
            ["Notifications"] = "5.2.0/notifications",
            ["Contacts"] = "5.2.0/contacts",
            ["Comments"] = "5.2.0/253-comments",
            ["Documents"] = "5.2.0/585-documents",
            ["Tagging"] = "5.3.0/252-tagging",
            ["Services"] = "5.3.0/833-services",
            ["core"] = "5.2.0/580-stock-reservations",
        };
        // A real 5.4 database had Marten's immutable cast helpers, and the removed scripts index through them.
        await database.ExecuteAsync(
            "create function public.mt_immutable_timestamp(value text) returns timestamp without time zone "
            + "language sql immutable as $f$ select value::timestamp $f$;");
        await database.ExecuteAsync(
            "create function public.mt_immutable_timestamptz(value text) returns timestamp with time zone "
            + "language sql immutable as $f$ select value::timestamptz $f$;");
        foreach (var (owner, file) in owners)
        {
            await database.ExecuteAsync(await File.ReadAllTextAsync(Path.Combine(removed, file + ".sql")));
            var id = file[(file.IndexOf('/') + 1)..];
            var version = file[..file.IndexOf('/')];
            await database.ExecuteAsync(
                $"insert into public.barako_migrations (owner, id, checksum, state, recorded_by) "
                + $"values ('{owner}', '{version}/{id}', 'recorded-by-5.4', 'applied', 'tests')");
        }

        string[] tables =
        [
            "mt_doc_public_forms", "mt_doc_booking_resources", "mt_doc_payments_attempts", "mt_doc_report_definitions",
            "mt_doc_scheduled_entries", "mt_doc_notifications", "mt_doc_contacts", "mt_doc_comments",
            "mt_doc_document_templates", "mt_doc_tagging_vocabularies", "mt_doc_service_registrations",
            "barako_stock_counters", "barako_stock_holds", "barako_stock_movements",
        ];
        foreach (var table in tables)
            (await database.IsTrueAsync($"select to_regclass('public.{table}') is not null")).Should().BeTrue($"{table} is the setup");

        var migrate = await RunHostAsync(assembly, database.ConnectionString, "db-migrate");
        var status = await RunHostAsync(assembly, database.ConnectionString, "db-migrate", "--status");

        migrate.ExitCode.Should().Be(0, $"output:\n{Truncate(migrate.Output)}");
        status.ExitCode.Should().Be(0, $"output:\n{Truncate(status.Output)}");
        status.Output.Should().Contain("not-loaded Forms/4.2.0/forms-public-forms");
        status.Output.Should().Contain("not-loaded core/5.2.0/580-stock-reservations");
        foreach (var table in tables)
            (await database.IsTrueAsync($"select to_regclass('public.{table}') is not null")).Should().BeTrue($"{table} is kept");
        (await database.IsTrueAsync(
            $"select count(*) = {owners.Count} from public.barako_migrations where recorded_by = 'tests'"))
            .Should().BeTrue("the rows of the removed modules are left in the ledger");
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }

    // JasperFx opens every dll next to the host looking for its extension attribute. The test
    // output holds the xunit assemblies, which barakoCMS.deps.json does not list, so on macOS the
    // load fails and db-apply exits 1. The core project's own output holds only what it ships.
    private static string CoreHostAssemblyPath() => ProjectOutputPath("barakoCMS", "barakoCMS.dll") ?? CoreAssembly;

    private static string SuiteAssemblyPath() =>
        ProjectOutputPath("BarakoCMS.Suite", "BarakoCMS.Suite.dll") ?? "BarakoCMS.Suite.dll (repository root not found)";

    private static string? ProjectOutputPath(string project, string assembly)
    {
        var output = AppContext.BaseDirectory;
        var root = new DirectoryInfo(output);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Directory.Build.props")))
            root = root.Parent;
        if (root is null)
            return null;

        var relative = Path.GetRelativePath(Path.Combine(root.FullName, "BarakoCMS.Tests"), output);
        return Path.Combine(root.FullName, project, relative, assembly);
    }

    private static async Task<(int ExitCode, string Output)> RunHostAsync(
        string hostAssembly, string connectionString, params string[] args)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add(hostAssembly);
        foreach (var arg in args)
            start.ArgumentList.Add(arg);

        // Built explicitly, not inherited: the fixture sets DATABASE_URL on this process, and an
        // inherited one would point the child at the shared database.
        start.Environment.Clear();
        start.Environment["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin";
        start.Environment["HOME"] = Environment.GetEnvironmentVariable("HOME") ?? "/tmp";
        start.Environment["DOTNET_ROOT"] = Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? string.Empty;
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        start.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";
        // Each child would otherwise take an inotify instance to watch its configuration files, and
        // a full run on a small box sits close to the per-user limit already.
        start.Environment["DOTNET_USE_POLLING_FILE_WATCHER"] = "1";
        start.Environment["DOTNET_hostBuilder__reloadConfigOnChange"] = "false";
        start.Environment["SKIP_SEEDER"] = "true";
        start.Environment["Kubernetes__Enabled"] = "false";
        start.Environment["JWT__Key"] = IntegrationTestFixture.JwtKey;
        start.Environment["ConnectionStrings__DefaultConnection"] = connectionString;

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("The host did not exit, so it served instead of running the command.");
        }

        return (process.ExitCode, await stdout + await stderr);
    }

    private static string Truncate(string text) =>
        text.Length <= 4000 ? text : text[^4000..];
}
