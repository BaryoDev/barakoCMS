using barakoCMS.Infrastructure.Migrations;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// The migration ledger from issue #901: which files a run executes, which it only records, and
/// what it leaves behind when it stops.
/// </summary>
/// <remarks>
/// Every case runs against a database of its own (see <see cref="MigrationScratchDatabase"/>). The
/// test migrations insert into <c>public.runs</c>, so "ran once" is a row count rather than
/// something the ledger says about itself.
/// </remarks>
[Collection("Sequential")]
public class MigrationLedgerTests
{
    private readonly IntegrationTestFixture _factory;

    public MigrationLedgerTests(IntegrationTestFixture factory) => _factory = factory;

    private static ShippedMigration Insert(string name, string owner = ShippedMigrations.CoreOwner) =>
        ShippedMigrations.Parse(owner, $"4.6.0/{name}.sql", $"insert into public.runs (name) values ('{name}');");

    private static void Quiet(string line)
    {
    }

    private const string SleepingSession =
        "exists (select 1 from pg_stat_activity where datname = current_database() "
        + "and pid <> pg_backend_pid() and state = 'active' and query like '%pg_sleep(60)%')";

    private const string SleepingSessionSql = "select " + SleepingSession;

    [Fact]
    public async Task Applying_twice_runs_each_migration_once()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await MigrationScratchDatabase.CreateAsync(_factory);
        ShippedMigration[] shipped = [Insert("first"), Insert("second")];
        var ledger = database.Ledger();

        var first = await ledger.ApplyAsync(shipped, Quiet, ct);
        var second = await ledger.ApplyAsync(shipped, Quiet, ct);

        first.Outcome.Should().Be(MigrationRunOutcome.Completed);
        first.Applied.Should().Equal("core/4.6.0/first", "core/4.6.0/second");
        second.Outcome.Should().Be(MigrationRunOutcome.Completed);
        second.Applied.Should().BeEmpty("the ledger already holds both");
        second.Baselined.Should().BeEmpty();
        (await database.RunsAsync()).Should().Equal(new[] { "first", "second" }, "a second run that executed anything would add a row");
        (await database.LedgerAsync()).Should().Equal("core/4.6.0/first applied", "core/4.6.0/second applied");
    }

    [Fact]
    public async Task A_file_edited_after_it_was_applied_stops_the_run_before_anything_runs()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await MigrationScratchDatabase.CreateAsync(_factory);
        var ledger = database.Ledger();
        var original = Insert("first");
        await ledger.ApplyAsync([original], Quiet, ct);

        var edited = ShippedMigrations.Parse(
            ShippedMigrations.CoreOwner, "4.6.0/first.sql", original.Sql + "\ninsert into public.runs (name) values ('edit');");
        var result = await ledger.ApplyAsync([edited, Insert("second")], Quiet, ct);

        result.Outcome.Should().Be(MigrationRunOutcome.ChecksumMismatch);
        result.Changed.Should().HaveCount(1);
        result.Changed[0].Migration.Key.Should().Be("core/4.6.0/first");
        result.Changed[0].Row!.Checksum.Should().Be(original.Checksum);
        result.Changed[0].Migration.Checksum.Should().NotBe(original.Checksum);
        (await database.RunsAsync()).Should().Equal(new[] { "first" }, "neither the edit nor the file after it may run");
        (await database.LedgerAsync()).Should().Equal("core/4.6.0/first applied");
    }

    [Fact]
    public async Task A_file_whose_skip_query_is_true_is_recorded_as_baselined_and_not_run()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await MigrationScratchDatabase.CreateAsync(_factory);
        var present = ShippedMigrations.Parse(ShippedMigrations.CoreOwner, "4.5.0/present.sql",
            "-- barako:skip-when: select to_regclass('public.runs') is not null\n"
            + "insert into public.runs (name) values ('present');");
        var absent = ShippedMigrations.Parse(ShippedMigrations.CoreOwner, "4.5.0/absent.sql",
            "-- barako:skip-when: select to_regclass('public.made_by_absent') is not null\n"
            + "create table public.made_by_absent (n int);\n"
            + "insert into public.runs (name) values ('absent');");
        var lines = new List<string>();

        var result = await database.Ledger().ApplyAsync([absent, present], lines.Add, ct);

        result.Outcome.Should().Be(MigrationRunOutcome.Completed);
        result.Baselined.Should().Equal("core/4.5.0/present");
        result.Applied.Should().Equal("core/4.5.0/absent");
        (await database.RunsAsync()).Should().Equal(new[] { "absent" }, "the file whose change was already in place must not run");
        (await database.LedgerAsync()).Should().Equal("core/4.5.0/absent applied", "core/4.5.0/present baselined");
        lines.Should().HaveCount(2);
        lines.Should().Contain(l => l.StartsWith("baselined") && l.Contains("core/4.5.0/present"),
            "a file recorded without being run is said out loud");
    }

    [Fact]
    public async Task A_database_nothing_has_started_against_gets_every_migration_recorded_and_none_run()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await MigrationScratchDatabase.CreateAsync(_factory, started: false);

        var result = await database.Ledger().ApplyAsync([Insert("first"), Insert("second")], Quiet, ct);

        result.Outcome.Should().Be(MigrationRunOutcome.Completed);
        result.Applied.Should().BeEmpty();
        result.Baselined.Should().Equal("core/4.6.0/first", "core/4.6.0/second");
        (await database.RunsAsync()).Should().BeEmpty("the first start creates the schema current, so no file applies");
        (await database.LedgerAsync()).Should().Equal("core/4.6.0/first baselined", "core/4.6.0/second baselined");
    }

    [Fact]
    public async Task A_file_that_fails_part_way_leaves_no_change_and_no_row_and_runs_from_the_top_next_time()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await MigrationScratchDatabase.CreateAsync(_factory);
        await database.ExecuteAsync("create table public.gate (n int)");
        var guarded = ShippedMigrations.Parse(ShippedMigrations.CoreOwner, "4.6.0/guarded.sql",
            "insert into public.runs (name) values ('guarded');\n"
            + "select 1 / (select count(*) from public.gate);");
        ShippedMigration[] shipped = [Insert("before"), guarded, Insert("zz-after")];
        var ledger = database.Ledger();

        var failed = await ledger.ApplyAsync(shipped, Quiet, ct);

        failed.Outcome.Should().Be(MigrationRunOutcome.Failed);
        failed.FailedKey.Should().Be("core/4.6.0/guarded");
        failed.Error.Should().Contain("22012", "the database's own error is what the operator needs");
        failed.Applied.Should().Equal("core/4.6.0/before");
        (await database.RunsAsync()).Should().Equal(new[] { "before" },
            "the failed file's insert rolls back with it, and the file after it does not run");
        (await database.LedgerAsync()).Should().Equal("core/4.6.0/before applied");

        await database.ExecuteAsync("insert into public.gate (n) values (1)");
        var retried = await ledger.ApplyAsync(shipped, Quiet, ct);

        retried.Outcome.Should().Be(MigrationRunOutcome.Completed);
        retried.Applied.Should().Equal("core/4.6.0/guarded", "core/4.6.0/zz-after");
        (await database.RunsAsync()).Should().Equal(new[] { "before", "guarded", "zz-after" },
            "the rerun starts the failed file again and leaves the one already applied alone");
    }

    [Fact]
    public async Task A_run_is_refused_while_another_holds_the_lock_and_changes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await MigrationScratchDatabase.CreateAsync(_factory);
        var ledger = database.Ledger();

        await using (var holder = new NpgsqlConnection(database.ConnectionString))
        {
            await holder.OpenAsync(ct);
            await using (var take = new NpgsqlCommand("select pg_advisory_lock(@key)", holder))
            {
                take.Parameters.AddWithValue("key", MigrationLedger.LockKey);
                await take.ExecuteScalarAsync(ct);
            }

            var refused = await ledger.ApplyAsync([Insert("first")], Quiet, ct);

            refused.Outcome.Should().Be(MigrationRunOutcome.LockBusy);
            (await database.RunsAsync()).Should().BeEmpty();
            (await database.LedgerAsync()).Should().BeEmpty("a refused run does not even create the table");
            (await ledger.RecordAsync(Insert("first"), ct)).Should().BeFalse();
            (await ledger.ForgetAsync("core", "4.6.0/first", ct)).Should().BeNull();
        }

        var after = await ledger.ApplyAsync([Insert("first")], Quiet, ct);

        after.Outcome.Should().Be(MigrationRunOutcome.Completed);
        (await database.RunsAsync()).Should().Equal("first");
    }

    [Fact]
    public async Task What_a_file_raises_as_a_notice_reaches_the_operator()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await MigrationScratchDatabase.CreateAsync(_factory);
        var talkative = ShippedMigrations.Parse(ShippedMigrations.CoreOwner, "4.6.0/talkative.sql",
            "do $$ begin raise notice 'role 42 was left alone'; end $$;");
        var lines = new List<string>();

        var result = await database.Ledger().ApplyAsync([talkative], lines.Add, ct);

        result.Outcome.Should().Be(MigrationRunOutcome.Completed);
        lines.Should().HaveCount(2);
        lines[0].Should().StartWith("notice").And.Contain("core/4.6.0/talkative").And.Contain("role 42 was left alone");
        lines[1].Should().StartWith("applied");
    }

    [Fact]
    public async Task A_file_marked_no_transaction_runs_outside_one()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await MigrationScratchDatabase.CreateAsync(_factory);
        var concurrently = ShippedMigrations.Parse(ShippedMigrations.CoreOwner, "4.6.0/runs-index.sql",
            "-- barako:no-transaction\n-- barako:rerunnable\n"
            + "create index concurrently if not exists runs_idx_name on public.runs (name);");

        var result = await database.Ledger().ApplyAsync([concurrently], Quiet, ct);

        result.Error.Should().BeNull("CREATE INDEX CONCURRENTLY fails with 25001 inside a transaction block");
        result.Outcome.Should().Be(MigrationRunOutcome.Completed);
        (await database.IsTrueAsync("select to_regclass('public.runs_idx_name') is not null")).Should().BeTrue();
        (await database.LedgerAsync()).Should().Equal("core/4.6.0/runs-index applied");
    }

    [Fact]
    public async Task The_Files_index_migration_runs_once_and_is_recorded_under_Files()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await MigrationScratchDatabase.CreateAsync(_factory);
        await database.ExecuteAsync("create table public.mt_doc_stored_files (id uuid primary key, data jsonb not null)");
        var shipped = ShippedMigrations.FromAssembly("Files", typeof(BarakoCMS.Files.FilesModule).Assembly);
        var ledger = database.Ledger();

        var first = await ledger.ApplyAsync(shipped, Quiet, ct);
        var second = await ledger.ApplyAsync(shipped, Quiet, ct);

        first.Error.Should().BeNull();
        first.Applied.Should().Equal("Files/4.2.0/stored-files-parent-index");
        second.Applied.Should().BeEmpty();
        second.Baselined.Should().BeEmpty();
        (await database.IsTrueAsync("select to_regclass('public.mt_doc_stored_files_idx_parent_file_id') is not null"))
            .Should().BeTrue("the module's own file is what built the index");
        (await database.LedgerAsync()).Should().Equal("Files/4.2.0/stored-files-parent-index applied");
    }

    [Fact]
    public async Task The_Files_index_migration_is_not_run_where_the_module_has_no_table_yet()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await MigrationScratchDatabase.CreateAsync(_factory);
        var shipped = ShippedMigrations.FromAssembly("Files", typeof(BarakoCMS.Files.FilesModule).Assembly);

        var result = await database.Ledger().ApplyAsync(shipped, Quiet, ct);

        result.Error.Should().BeNull("without the skip query the file fails on the missing table");
        result.Applied.Should().BeEmpty();
        result.Baselined.Should().Equal("Files/4.2.0/stored-files-parent-index");
    }

    /// <summary>
    /// An index build that was interrupted leaves an invalid index under its name. The file's
    /// <c>IF NOT EXISTS</c> then skips, and without the check after the run the ledger would say the
    /// index was built.
    /// </summary>
    [Fact]
    public async Task An_invalid_index_left_by_an_interrupted_build_fails_the_Files_migration_until_it_is_dropped()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await MigrationScratchDatabase.CreateAsync(_factory);
        await database.ExecuteAsync("create table public.mt_doc_stored_files (id uuid primary key, data jsonb not null)");
        await database.ExecuteAsync(
            "insert into public.mt_doc_stored_files (id, data) values "
            + "(gen_random_uuid(), '{\"ParentFileId\": \"00000000-0000-0000-0000-000000000001\"}'), "
            + "(gen_random_uuid(), '{\"ParentFileId\": \"00000000-0000-0000-0000-000000000001\"}')");
        var interrupted = () => database.ExecuteAsync(
            "create unique index concurrently mt_doc_stored_files_idx_parent_file_id "
            + "on public.mt_doc_stored_files ((data ->> 'ParentFileId'))");
        await interrupted.Should().ThrowAsync<PostgresException>("two rows share the value, so the unique build fails part way");
        (await database.IsTrueAsync(
            "select exists (select 1 from pg_index x join pg_class c on c.oid = x.indexrelid "
            + "where c.relname = 'mt_doc_stored_files_idx_parent_file_id' and not x.indisvalid)"))
            .Should().BeTrue("a failed CONCURRENTLY build leaves its index behind, invalid");
        var shipped = ShippedMigrations.FromAssembly("Files", typeof(BarakoCMS.Files.FilesModule).Assembly);
        var ledger = database.Ledger();

        var stuck = await ledger.ApplyAsync(shipped, Quiet, ct);

        stuck.Outcome.Should().Be(MigrationRunOutcome.Failed);
        stuck.FailedKey.Should().Be("Files/4.2.0/stored-files-parent-index");
        stuck.Error.Should().Contain("invalid index");
        (await database.LedgerAsync()).Should().BeEmpty("an index that is not usable must not be recorded as built");

        await database.ExecuteAsync("drop index public.mt_doc_stored_files_idx_parent_file_id");
        var repaired = await ledger.ApplyAsync(shipped, Quiet, ct);

        repaired.Error.Should().BeNull();
        repaired.Applied.Should().Equal("Files/4.2.0/stored-files-parent-index");
        (await database.IsTrueAsync(shipped[0].SkipWhen!)).Should().BeTrue();
    }

    [Fact]
    public async Task A_file_that_runs_and_leaves_its_skip_query_false_is_failed_and_rolled_back()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await MigrationScratchDatabase.CreateAsync(_factory);
        var hollow = ShippedMigrations.Parse(ShippedMigrations.CoreOwner, "4.6.0/hollow.sql",
            "-- barako:skip-when: select to_regclass('public.never_made') is not null\n"
            + "insert into public.runs (name) values ('hollow');");

        var result = await database.Ledger().ApplyAsync([hollow], Quiet, ct);

        result.Outcome.Should().Be(MigrationRunOutcome.Failed);
        result.FailedKey.Should().Be("core/4.6.0/hollow");
        result.Error.Should().Contain("skip-when query still answers false");
        (await database.RunsAsync()).Should().BeEmpty("the file's own work is rolled back with it");
        (await database.LedgerAsync()).Should().BeEmpty();
    }

    /// <summary>
    /// A baselined row means the file never ran here and never will, so an edit to it cannot have
    /// split what ran from what is recorded. It is said, and the run goes on. A row for a file that
    /// did run still stops everything, which the test above this region holds.
    /// </summary>
    [Fact]
    public async Task A_baselined_file_that_changed_is_reported_and_does_not_stop_the_run()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await MigrationScratchDatabase.CreateAsync(_factory, started: false);
        var ledger = database.Ledger();
        var original = Insert("first");
        await ledger.ApplyAsync([original], Quiet, ct);
        (await database.LedgerAsync()).Should().Equal("core/4.6.0/first baselined");
        await database.ExecuteAsync("create table public.mt_doc_users (id uuid primary key)");
        var edited = ShippedMigrations.Parse(ShippedMigrations.CoreOwner, "4.6.0/first.sql", original.Sql + "\n-- a later note");
        var lines = new List<string>();

        var result = await ledger.ApplyAsync([edited, Insert("second")], lines.Add, ct);
        var status = await ledger.StatusAsync([edited, Insert("second")], ct);

        result.Outcome.Should().Be(MigrationRunOutcome.Completed);
        result.Applied.Should().Equal("core/4.6.0/second");
        lines.Should().HaveCount(2);
        lines[0].Should().StartWith("warning").And.Contain("core/4.6.0/first")
            .And.Contain(original.Checksum).And.Contain(edited.Checksum);
        (await database.RunsAsync()).Should().Equal(new[] { "second" }, "the baselined file is still not run");
        status.Lines.Should().HaveCount(2);
        status.Lines[0].State.Should().Be(MigrationState.Baselined);
        status.Lines[0].FileChanged.Should().BeTrue();
        status.Current.Should().BeTrue("a changed file that never ran here does not hold a deploy");
    }

    /// <summary>
    /// A file has no time limit, so the token is the only bound. Cancelling it has to reach the
    /// server: a client that only stopped waiting would leave the statement running with its locks
    /// and the migration lock.
    /// </summary>
    [Fact]
    public async Task Cancelling_stops_the_statement_at_the_database_rolls_the_file_back_and_frees_the_lock()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await MigrationScratchDatabase.CreateAsync(_factory);
        var slow = ShippedMigrations.Parse(ShippedMigrations.CoreOwner, "4.6.0/slow.sql",
            "insert into public.runs (name) values ('slow');\nselect pg_sleep(60);");
        var ledger = database.Ledger();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var run = ledger.ApplyAsync([slow], Quiet, stop.Token);
        await database.WaitForAsync(SleepingSessionSql, "the file's pg_sleep running at the database");
        stop.Cancel();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(30), ct);

        result.Outcome.Should().Be(MigrationRunOutcome.Cancelled);
        result.FailedKey.Should().Be("core/4.6.0/slow");
        (await database.IsTrueAsync("select not " + SleepingSession))
            .Should().BeTrue("the server was told to stop, so nothing is still sleeping");
        (await database.RunsAsync()).Should().BeEmpty("the file's insert rolled back");
        (await database.LedgerAsync()).Should().BeEmpty();
        (await ledger.ApplyAsync([Insert("after")], Quiet, ct)).Outcome
            .Should().Be(MigrationRunOutcome.Completed, "the lock was released, so the next run is not told another holds it");
    }

    /// <summary>
    /// The connection dying under a file has to come back as that file failing. The unlock in the
    /// finally runs on the dead connection, and what that throws must not replace the result.
    /// </summary>
    [Fact]
    public async Task A_session_killed_under_a_file_is_reported_as_that_file_failing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await MigrationScratchDatabase.CreateAsync(_factory);
        var slow = ShippedMigrations.Parse(ShippedMigrations.CoreOwner, "4.6.0/slow.sql",
            "insert into public.runs (name) values ('slow');\nselect pg_sleep(60);");
        var ledger = database.Ledger();

        var run = ledger.ApplyAsync([Insert("before"), slow, Insert("zz-after")], Quiet, ct);
        await database.WaitForAsync(SleepingSessionSql, "the file's pg_sleep running at the database");
        await database.ExecuteAsync(
            "select pg_terminate_backend(pid) from pg_stat_activity where datname = current_database() "
            + "and pid <> pg_backend_pid() and state = 'active' and query like '%pg_sleep(60)%'");
        var result = await run.WaitAsync(TimeSpan.FromSeconds(30), ct);

        result.Outcome.Should().Be(MigrationRunOutcome.Failed);
        result.FailedKey.Should().Be("core/4.6.0/slow");
        result.Applied.Should().Equal("core/4.6.0/before");
        (await database.RunsAsync()).Should().Equal("before");
        (await database.LedgerAsync()).Should().Equal("core/4.6.0/before applied");
    }

    [Fact]
    public async Task A_transaction_open_elsewhere_is_called_out_before_the_first_file_runs()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await MigrationScratchDatabase.CreateAsync(_factory);
        var ledger = database.Ledger();
        var lines = new List<string>();

        await using (var serving = new NpgsqlConnection(database.ConnectionString))
        {
            await serving.OpenAsync(ct);
            await using var open = await serving.BeginTransactionAsync(ct);
            await using (var touch = new NpgsqlCommand("select 1", serving, open))
            {
                await touch.ExecuteScalarAsync(ct);
            }

            await ledger.ApplyAsync([Insert("first")], lines.Add, ct);
            await ledger.ApplyAsync([Insert("first")], lines.Add, ct);
        }

        await ledger.ApplyAsync([Insert("first"), Insert("second")], lines.Add, ct);

        lines.Should().HaveCount(3, "one warning and one applied line with the transaction open, nothing on the run with "
            + "no file to execute, and one applied line once the other session is gone");
        lines[0].Should().StartWith("warning").And.Contain("1 other session(s)").And.Contain("Stop the API");
        lines[1].Should().StartWith("applied").And.Contain("core/4.6.0/first");
        lines[2].Should().StartWith("applied").And.Contain("core/4.6.0/second");
    }

    [Fact]
    public async Task A_store_outside_the_public_schema_is_not_baselined_by_a_start()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await MigrationScratchDatabase.CreateAsync(_factory, started: false);

        var recorded = await database.Ledger("tenant_a").BaselineIfFreshAsync([Insert("first")], ct);

        recorded.Should().BeEmpty();
        (await database.LedgerAsync()).Should().BeEmpty(
            "public has no users table because the tables are elsewhere, which is not the same as a new database");
    }

    [Fact]
    public async Task A_recorded_migration_is_not_run_and_a_forgotten_one_runs_again()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await MigrationScratchDatabase.CreateAsync(_factory);
        var ledger = database.Ledger();
        var migration = Insert("by-hand");

        (await ledger.RecordAsync(migration, ct)).Should().BeTrue();
        var afterRecord = await ledger.ApplyAsync([migration], Quiet, ct);

        afterRecord.Applied.Should().BeEmpty();
        (await database.RunsAsync()).Should().BeEmpty("recording says the operator already applied it");
        (await database.LedgerAsync()).Should().Equal("core/4.6.0/by-hand recorded");

        (await ledger.ForgetAsync(migration.Owner, migration.Id, ct)).Should().Be(1);
        var afterForget = await ledger.ApplyAsync([migration], Quiet, ct);

        afterForget.Applied.Should().Equal("core/4.6.0/by-hand");
        (await database.RunsAsync()).Should().Equal("by-hand");
        (await database.LedgerAsync()).Should().Equal("core/4.6.0/by-hand applied");
    }

    [Fact]
    public async Task Recording_a_changed_file_replaces_its_checksum_so_the_next_run_goes_ahead()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await MigrationScratchDatabase.CreateAsync(_factory);
        var ledger = database.Ledger();
        var original = Insert("first");
        await ledger.ApplyAsync([original], Quiet, ct);
        var edited = ShippedMigrations.Parse(ShippedMigrations.CoreOwner, "4.6.0/first.sql", original.Sql + "\n-- a note");

        (await ledger.ApplyAsync([edited], Quiet, ct)).Outcome.Should().Be(MigrationRunOutcome.ChecksumMismatch);
        (await ledger.RecordAsync(edited, ct)).Should().BeTrue();
        var result = await ledger.ApplyAsync([edited, Insert("second")], Quiet, ct);

        result.Outcome.Should().Be(MigrationRunOutcome.Completed);
        result.Applied.Should().Equal("core/4.6.0/second");
        (await database.RunsAsync()).Should().Equal(new[] { "first", "second" }, "the recorded file is not run a second time");
    }

    [Fact]
    public async Task Status_names_what_is_pending_changed_and_not_shipped_without_writing_anything()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await MigrationScratchDatabase.CreateAsync(_factory);
        var ledger = database.Ledger();
        var kept = Insert("kept");
        var edited = Insert("edited");
        var dropped = Insert("module-file", owner: "Gone");

        var before = await ledger.StatusAsync([kept], ct);

        before.Lines.Should().HaveCount(1);
        before.Lines[0].State.Should().Be(MigrationState.Pending);
        before.Current.Should().BeFalse();
        (await database.IsTrueAsync("select to_regclass('public.barako_migrations') is null"))
            .Should().BeTrue("status reads, and the table is created by the first run");

        await ledger.ApplyAsync([kept, edited, dropped], Quiet, ct);
        var changedFile = ShippedMigrations.Parse(ShippedMigrations.CoreOwner, "4.6.0/edited.sql", edited.Sql + "\n-- later");
        var skippable = ShippedMigrations.Parse(ShippedMigrations.CoreOwner, "4.6.0/skippable.sql",
            "-- barako:skip-when: select true\nselect 1;");
        var after = await ledger.StatusAsync([changedFile, kept, Insert("new"), skippable], ct);

        after.Lines.Should().HaveCount(4);
        after.Lines.Select(l => $"{l.Migration.Key} {l.State}").Should().Equal(
            "core/4.6.0/edited changed",
            "core/4.6.0/kept applied",
            "core/4.6.0/new pending",
            "core/4.6.0/skippable not-needed");
        after.NotShipped.Should().HaveCount(1);
        after.NotShipped[0].Key.Should().Be("Gone/4.6.0/module-file", "a module that is switched off keeps its rows");
        after.Current.Should().BeFalse();
        (await ledger.StatusAsync([kept], ct)).Current.Should().BeTrue();
    }

    [Fact]
    public async Task A_starting_host_baselines_a_new_database_and_leaves_a_used_one_alone()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var unused = await MigrationScratchDatabase.CreateAsync(_factory, started: false);
        await using var used = await MigrationScratchDatabase.CreateAsync(_factory);
        ShippedMigration[] shipped = [Insert("first"), Insert("second")];

        var recorded = await unused.Ledger().BaselineIfFreshAsync(shipped, ct);
        var again = await unused.Ledger().BaselineIfFreshAsync(shipped, ct);
        var untouched = await used.Ledger().BaselineIfFreshAsync(shipped, ct);

        recorded.Should().Equal("core/4.6.0/first", "core/4.6.0/second");
        again.Should().BeEmpty("the rows are already there");
        (await unused.LedgerAsync()).Should().Equal("core/4.6.0/first baselined", "core/4.6.0/second baselined");
        (await unused.RunsAsync()).Should().BeEmpty();
        untouched.Should().BeEmpty();
        (await used.IsTrueAsync("select to_regclass('public.barako_migrations') is null"))
            .Should().BeTrue("a database with history is never marked as migrated by a start");
    }
}
