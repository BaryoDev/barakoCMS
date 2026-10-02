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
[Collection("Sequential")]
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
        ledgerAfterFirst.Count.Should().BeGreaterThanOrEqualTo(host == "suite" ? 10 : 7,
            "seven core files and three module files were released before the ledger");
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

        var apply = await RunHostAsync(CoreAssembly, database.ConnectionString, "db-apply");
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
            await store.Storage.Database.EnsureStorageExistsAsync(typeof(BarakoCMS.Forms.PublicForm), ct);
            await store.Storage.Database.EnsureStorageExistsAsync(typeof(BarakoCMS.Email.Resend.SentEmail), ct);
        }

        var preLedger = ShippedMigrations
            .Discover([new BarakoCMS.Forms.FormsModule(), new BarakoCMS.Files.FilesModule(), new BarakoCMS.Email.Resend.ResendEmailModule()])
            .Where(m => Version.Parse(m.Version) < new Version(4, 6, 0))
            .ToList();
        preLedger.Should().HaveCount(10);

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

    private static string SuiteAssemblyPath()
    {
        var output = AppContext.BaseDirectory;
        var root = new DirectoryInfo(output);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Directory.Build.props")))
            root = root.Parent;
        if (root is null)
            return "BarakoCMS.Suite.dll (repository root not found)";

        var relative = Path.GetRelativePath(Path.Combine(root.FullName, "BarakoCMS.Tests"), output);
        return Path.Combine(root.FullName, "BarakoCMS.Suite", relative, "BarakoCMS.Suite.dll");
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
