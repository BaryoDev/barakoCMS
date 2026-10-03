using barakoCMS.Infrastructure.Migrations;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// <c>db-migrate</c> as an operator meets it: the arguments, the lines printed and the exit code a
/// deploy script reads (issue #901).
/// </summary>
[Collection("Sequential")]
public class MigrationCommandTests
{
    private readonly IntegrationTestFixture _factory;

    public MigrationCommandTests(IntegrationTestFixture factory) => _factory = factory;

    private static ShippedMigration Insert(string name) =>
        ShippedMigrations.Parse(ShippedMigrations.CoreOwner, $"4.6.0/{name}.sql", $"insert into public.runs (name) values ('{name}');");

    private static async Task<(int Exit, string Output)> RunAsync(
        MigrationScratchDatabase database, IReadOnlyList<ShippedMigration> shipped, params string[] args)
    {
        var output = new StringWriter();
        var exit = await MigrationCommand.RunAsync(
            database.Ledger(), shipped, args, output, TestContext.Current.CancellationToken);
        return (exit, output.ToString());
    }

    [Fact]
    public void Only_a_first_argument_of_db_migrate_names_the_command()
    {
        MigrationCommand.IsNamed(["db-migrate"]).Should().BeTrue();
        MigrationCommand.IsNamed(["db-migrate", "--status"]).Should().BeTrue();
        MigrationCommand.IsNamed([]).Should().BeFalse();
        MigrationCommand.IsNamed(["db-assert"]).Should().BeFalse();
        MigrationCommand.IsNamed(["--urls", "db-migrate"]).Should().BeFalse("a flag first means the host serves");
    }

    [Fact]
    public async Task Status_exits_one_until_the_run_has_recorded_everything_and_zero_after()
    {
        await using var database = await MigrationScratchDatabase.CreateAsync(_factory);
        ShippedMigration[] shipped = [Insert("first"), Insert("second")];

        var before = await RunAsync(database, shipped, "--status");
        var run = await RunAsync(database, shipped);
        var after = await RunAsync(database, shipped, "--status");
        var rerun = await RunAsync(database, shipped);

        before.Exit.Should().Be(1);
        before.Output.Should().Contain("pending    core/4.6.0/first").And.Contain("2 pending");
        run.Exit.Should().Be(0);
        run.Output.Should().Contain("applied    core/4.6.0/first").And.Contain("applied    core/4.6.0/second");
        run.Output.Should().Contain("2 applied, 0 baselined, 0 already recorded.");
        after.Exit.Should().Be(0);
        after.Output.Should().Contain("applied    core/4.6.0/second").And.NotContain("pending");
        rerun.Exit.Should().Be(0);
        rerun.Output.Should().Contain("0 applied, 0 baselined, 2 already recorded.");
        (await database.RunsAsync()).Should().Equal("first", "second");
    }

    [Fact]
    public async Task A_failing_file_exits_one_and_names_the_file_and_the_database_error()
    {
        await using var database = await MigrationScratchDatabase.CreateAsync(_factory);
        var broken = ShippedMigrations.Parse(ShippedMigrations.CoreOwner, "4.6.0/broken.sql", "select 1 / 0;");

        var run = await RunAsync(database, [Insert("first"), broken]);

        run.Exit.Should().Be(1);
        run.Output.Should().Contain("applied    core/4.6.0/first");
        run.Output.Should().Contain("failed     core/4.6.0/broken").And.Contain("22012");
        (await database.LedgerAsync()).Should().Equal("core/4.6.0/first applied");
    }

    [Fact]
    public async Task A_changed_file_exits_one_and_prints_both_checksums()
    {
        await using var database = await MigrationScratchDatabase.CreateAsync(_factory);
        var original = Insert("first");
        await RunAsync(database, [original]);
        var edited = ShippedMigrations.Parse(ShippedMigrations.CoreOwner, "4.6.0/first.sql", original.Sql + "\n-- edited");

        var run = await RunAsync(database, [edited, Insert("second")]);
        var status = await RunAsync(database, [edited], "--status");

        run.Exit.Should().Be(1);
        run.Output.Should().Contain("changed    core/4.6.0/first");
        run.Output.Should().Contain(original.Checksum).And.Contain(edited.Checksum);
        run.Output.Should().Contain("Nothing was run");
        status.Exit.Should().Be(1);
        status.Output.Should().Contain("changed    core/4.6.0/first");
        (await database.RunsAsync()).Should().Equal("first");
    }

    [Fact]
    public async Task A_held_lock_exits_one_and_says_so()
    {
        await using var database = await MigrationScratchDatabase.CreateAsync(_factory);
        await using var holder = new NpgsqlConnection(database.ConnectionString);
        await holder.OpenAsync(TestContext.Current.CancellationToken);
        await using (var take = new NpgsqlCommand("select pg_advisory_lock(@key)", holder))
        {
            take.Parameters.AddWithValue("key", MigrationLedger.LockKey);
            await take.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        }

        var run = await RunAsync(database, [Insert("first")]);
        await holder.CloseAsync();

        run.Exit.Should().Be(1);
        run.Output.Should().Contain("holds the migration lock");
        (await database.RunsAsync()).Should().BeEmpty();
    }

    /// <summary>
    /// The shipped files and the ledger name <c>public</c>. On a store that keeps its tables elsewhere
    /// the "new database" check would find no users table and record everything as done.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("--status")]
    [InlineData("--record core/4.6.0/first")]
    public async Task A_store_outside_the_public_schema_is_refused_and_nothing_is_recorded(string arguments)
    {
        await using var database = await MigrationScratchDatabase.CreateAsync(_factory, started: false);
        var output = new StringWriter();

        var exit = await MigrationCommand.RunAsync(
            database.Ledger("tenant_a"),
            [Insert("first")],
            arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries),
            output,
            TestContext.Current.CancellationToken);

        exit.Should().Be(1);
        output.ToString().Should().Contain("schema other than public").And.Contain("Nothing was read, run or recorded");
        (await database.LedgerAsync()).Should().BeEmpty();
    }

    [Theory]
    [InlineData("--baseline 4.5.0")]
    [InlineData("--record")]
    [InlineData("--status now")]
    [InlineData("apply")]
    public async Task Arguments_it_does_not_know_exit_one_print_the_usage_and_change_nothing(string arguments)
    {
        await using var database = await MigrationScratchDatabase.CreateAsync(_factory);

        var run = await RunAsync(database, [Insert("first")], arguments.Split(' '));

        run.Exit.Should().Be(1);
        run.Output.Should().Contain("db-migrate --status");
        (await database.RunsAsync()).Should().BeEmpty();
        (await database.LedgerAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Record_and_forget_take_a_shipped_key_and_refuse_any_other()
    {
        await using var database = await MigrationScratchDatabase.CreateAsync(_factory);
        ShippedMigration[] shipped = [Insert("first")];

        var unknown = await RunAsync(database, shipped, "--record", "core/4.6.0/typo'; drop table x");
        var recorded = await RunAsync(database, shipped, "--record", "core/4.6.0/first");
        var forgotten = await RunAsync(database, shipped, "--forget", "core/4.6.0/first");
        var forgottenAgain = await RunAsync(database, shipped, "--forget", "core/4.6.0/first");
        var wrongCase = await RunAsync(database, shipped, "--forget", "Core/4.6.0/first");

        unknown.Exit.Should().Be(1);
        unknown.Output.Should().Contain("  core/4.6.0/first", "the keys this build ships are listed");
        unknown.Output.Should().NotContain("typo", "what was typed matched nothing and is not echoed");
        recorded.Exit.Should().Be(0);
        recorded.Output.Should().Contain("recorded   core/4.6.0/first");
        forgotten.Exit.Should().Be(0);
        forgotten.Output.Should().Contain("forgot     core/4.6.0/first");
        forgottenAgain.Exit.Should().Be(0);
        forgottenAgain.Output.Should().Contain("had no row");
        wrongCase.Exit.Should().Be(1, "a key is matched exactly, the way the ledger stores it");
        (await database.RunsAsync()).Should().BeEmpty("neither command runs a file");
        (await database.LedgerAsync()).Should().BeEmpty();
    }
}
