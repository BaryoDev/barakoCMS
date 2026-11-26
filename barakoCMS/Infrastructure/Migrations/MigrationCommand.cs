using Marten;
using Npgsql;

namespace barakoCMS.Infrastructure.Migrations;

/// <summary>
/// <c>db-migrate</c>: what the operator types, what they are told, and the exit code a deploy reads.
/// </summary>
internal static class MigrationCommand
{
    public const string Name = "db-migrate";

    private const string Usage =
        """
        db-migrate                       run every migration the ledger lacks, in order, then create missing tables
        db-migrate --status              list each migration and each missing table, change nothing
        db-migrate --record <key>        record a migration you applied by hand, without running it
        db-migrate --forget <key>        remove a migration's row, after rolling it back by hand

        A key is owner/version/name as --status prints it, such as core/4.2.0/site-share-links.
        Stop the API before a run that has files to execute. --status only reads.
        Exit code 0 means the ledger matches this build. Anything else is 1.
        """;

    private const string LockBusy =
        "Another db-migrate run holds the migration lock on this database. Nothing was changed. "
        + "Run this again when it has finished. If no run is alive, a session left by one that was killed "
        + "is still working at the database: docs/migrations.md, \"A run that was killed\", has the query that finds it.";

    private const string SchemaNotPublic =
        "This store keeps its tables in a schema other than public. The ledger and every shipped migration "
        + "name public, so nothing here can tell what this database has had. Nothing was read, run or recorded. "
        + "Apply the files under migrations/ by hand against your schema.";

    public static bool IsNamed(string[] args) => args.Length > 0 && args[0] == Name;

    /// <param name="args">The arguments after <c>db-migrate</c>.</param>
    /// <param name="store">
    /// The store whose missing tables and functions are listed by <c>--status</c> and created after the ledger run.
    /// Null leaves the schema alone, which is what a test of the ledger alone wants.
    /// </param>
    /// <param name="configuration">Read for the schema lock timeout.</param>
    public static async Task<int> RunAsync(
        MigrationLedger ledger,
        IReadOnlyList<ShippedMigration> shipped,
        string[] args,
        TextWriter output,
        CancellationToken ct = default,
        IDocumentStore? store = null,
        IConfiguration? configuration = null)
    {
        if (!ledger.SchemaSupported)
        {
            output.WriteLine(SchemaNotPublic);
            return 1;
        }

        try
        {
            return await DispatchAsync(ledger, shipped, args, output, store, configuration, ct);
        }
        catch (InvalidOperationException ex)
        {
            // A lock refused behind a pooler (Leadership:RefuseBehindPooler) is a result, not a crash.
            output.WriteLine(ex.Message);
            return 1;
        }
    }

    private static Task<int> DispatchAsync(
        MigrationLedger ledger, IReadOnlyList<ShippedMigration> shipped, string[] args, TextWriter output,
        IDocumentStore? store, IConfiguration? configuration, CancellationToken ct)
    {
        switch (args)
        {
            case []:
                return ApplyAsync(ledger, shipped, output, store, configuration, ct);
            case ["--status"]:
                return StatusAsync(ledger, shipped, output, store, ct);
            case ["--record", var key]:
                return RecordAsync(ledger, shipped, key, output, ct);
            case ["--forget", var key]:
                return ForgetAsync(ledger, shipped, key, output, ct);
            default:
                output.WriteLine(Usage);
                return Task.FromResult(1);
        }
    }

    private static async Task<int> ApplyAsync(
        MigrationLedger ledger, IReadOnlyList<ShippedMigration> shipped, TextWriter output, IDocumentStore? store,
        IConfiguration? configuration, CancellationToken ct)
    {
        var result = await ledger.ApplyAsync(shipped, output.WriteLine, ct);

        switch (result.Outcome)
        {
            case MigrationRunOutcome.LockBusy:
                output.WriteLine(LockBusy);
                return 1;

            case MigrationRunOutcome.ChecksumMismatch:
                WriteChanged(result.Changed, output);
                output.WriteLine(
                    "Nothing was run. A migration that has been recorded is never edited: a further change "
                    + "ships as a new file. If the file this build ships is the right one and the database "
                    + "already has its change, run db-migrate --record <key> to record it again.");
                return 1;

            case MigrationRunOutcome.Cancelled:
                output.WriteLine(result.FailedKey is null
                    ? "cancelled  between files. Every migration listed above stays in place with its row."
                    : $"cancelled  {result.FailedKey}  The statement in flight was cancelled at the database. A file that "
                      + "runs in a transaction was rolled back; it has no row and runs from the top next time. A "
                      + "no-transaction file may have left an index half built: the next run says so and names the fix.");
                return 1;

            case MigrationRunOutcome.Failed:
                output.WriteLine($"failed     {result.FailedKey}  {result.Error}");
                output.WriteLine(
                    "Stopped at the first failure. Every migration listed above it stays in place with its "
                    + "row. The failed one has no row and runs from the top next time.");
                return 1;

            default:
                var recorded = shipped.Count - result.Applied.Count - result.Baselined.Count;
                output.WriteLine(
                    $"{result.Applied.Count} applied, {result.Baselined.Count} baselined, {recorded} already recorded.");
                return store is null ? 0 : await MissingSchemaObjects.CreateAndReportAsync(store, configuration, output, ct);
        }
    }

    private static async Task<int> StatusAsync(
        MigrationLedger ledger, IReadOnlyList<ShippedMigration> shipped, TextWriter output,
        IDocumentStore? store, CancellationToken ct)
    {
        var status = await ledger.StatusAsync(shipped, ct);

        foreach (var line in status.Lines)
        {
            output.WriteLine(line.Row is null
                ? $"{line.State,-10} {line.Migration.Key}"
                : $"{line.State,-10} {line.Migration.Key}  {line.Row.RecordedAt} UTC"
                  + (line.State != MigrationState.Changed && line.FileChanged
                      ? "  (never run here, and the file has changed since it was baselined)"
                      : string.Empty));
        }

        foreach (var row in status.NotShipped)
            output.WriteLine($"not-loaded {row.Key}  {row.State} {row.RecordedAt} UTC, and this build does not ship it");

        WriteChanged(status.In(MigrationState.Changed), output);

        IReadOnlyList<string> missing;
        try
        {
            missing = store is null ? [] : await MissingSchemaObjects.PendingAsync(store, ct);
        }
        catch (Exception ex) when (ex is OperationCanceledException or NpgsqlException)
        {
            output.WriteLine(ct.IsCancellationRequested
                ? "cancelled  while reading missing schema objects."
                : $"failed     reading missing schema objects  {MigrationLedger.Describe(ex)}");
            return 1;
        }

        foreach (var name in missing)
        {
            output.WriteLine($"pending    create {name}  (missing from the database, db-migrate creates it)");
        }

        var pending = status.In(MigrationState.Pending).Count + missing.Count;
        var notNeeded = status.In(MigrationState.NotNeeded).Count;
        if (pending + notNeeded > 0)
        {
            output.WriteLine(
                $"{pending} pending, {notNeeded} not needed and not yet recorded. Run db-migrate.");
        }

        return status.Current && missing.Count == 0 ? 0 : 1;
    }

    private static async Task<int> RecordAsync(
        MigrationLedger ledger, IReadOnlyList<ShippedMigration> shipped, string key, TextWriter output, CancellationToken ct)
    {
        var migration = shipped.FirstOrDefault(m => m.Key == key);
        if (migration is null)
        {
            WriteUnknown(shipped, output);
            return 1;
        }

        if (!await ledger.RecordAsync(migration, ct))
        {
            output.WriteLine(LockBusy);
            return 1;
        }

        output.WriteLine($"recorded   {migration.Key}  (not run)");
        return 0;
    }

    private static async Task<int> ForgetAsync(
        MigrationLedger ledger, IReadOnlyList<ShippedMigration> shipped, string key, TextWriter output, CancellationToken ct)
    {
        // Only a shipped key, so the row removed is one this build can put back.
        var migration = shipped.FirstOrDefault(m => m.Key == key);
        if (migration is null)
        {
            WriteUnknown(shipped, output);
            return 1;
        }

        var removed = await ledger.ForgetAsync(migration.Owner, migration.Id, ct);
        if (removed is null)
        {
            output.WriteLine(LockBusy);
            return 1;
        }

        output.WriteLine(removed == 0
            ? $"{migration.Key} had no row. Nothing was changed."
            : $"forgot     {migration.Key}  (the next db-migrate treats it as new)");
        return 0;
    }

    private static void WriteChanged(IReadOnlyList<MigrationStatusLine> changed, TextWriter output)
    {
        foreach (var line in changed)
        {
            output.WriteLine(
                $"changed    {line.Migration.Key}  recorded {line.Row!.Checksum}, this build ships {line.Migration.Checksum}");
        }
    }

    // The key that was typed is not echoed: it has not matched anything this build knows.
    private static void WriteUnknown(IReadOnlyList<ShippedMigration> shipped, TextWriter output)
    {
        output.WriteLine("That key is not a migration this build ships. The keys are:");
        foreach (var migration in shipped)
            output.WriteLine($"  {migration.Key}");
    }
}
