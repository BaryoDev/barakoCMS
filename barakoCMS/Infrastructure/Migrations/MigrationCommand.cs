namespace barakoCMS.Infrastructure.Migrations;

/// <summary>
/// <c>db-migrate</c>: what the operator types, what they are told, and the exit code a deploy reads.
/// </summary>
internal static class MigrationCommand
{
    public const string Name = "db-migrate";

    private const string Usage =
        """
        db-migrate                       run every migration the ledger lacks, in order
        db-migrate --status              list each migration and its state, change nothing
        db-migrate --record <key>        record a migration you applied by hand, without running it
        db-migrate --forget <key>        remove a migration's row, after rolling it back by hand

        A key is owner/version/name as --status prints it, such as core/4.2.0/site-share-links.
        Exit code 0 means the ledger matches this build. Anything else is 1.
        """;

    private const string LockBusy =
        "Another db-migrate run holds the migration lock on this database. Nothing was changed. "
        + "Run this again when it has finished.";

    public static bool IsNamed(string[] args) => args.Length > 0 && args[0] == Name;

    /// <param name="args">The arguments after <c>db-migrate</c>.</param>
    public static async Task<int> RunAsync(
        MigrationLedger ledger,
        IReadOnlyList<ShippedMigration> shipped,
        string[] args,
        TextWriter output,
        CancellationToken ct = default)
    {
        switch (args)
        {
            case []:
                return await ApplyAsync(ledger, shipped, output, ct);
            case ["--status"]:
                return await StatusAsync(ledger, shipped, output, ct);
            case ["--record", var key]:
                return await RecordAsync(ledger, shipped, key, output, ct);
            case ["--forget", var key]:
                return await ForgetAsync(ledger, shipped, key, output, ct);
            default:
                output.WriteLine(Usage);
                return 1;
        }
    }

    private static async Task<int> ApplyAsync(
        MigrationLedger ledger, IReadOnlyList<ShippedMigration> shipped, TextWriter output, CancellationToken ct)
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
                return 0;
        }
    }

    private static async Task<int> StatusAsync(
        MigrationLedger ledger, IReadOnlyList<ShippedMigration> shipped, TextWriter output, CancellationToken ct)
    {
        var status = await ledger.StatusAsync(shipped, ct);

        foreach (var line in status.Lines)
        {
            output.WriteLine(line.Row is null
                ? $"{line.State,-10} {line.Migration.Key}"
                : $"{line.State,-10} {line.Migration.Key}  {line.Row.RecordedAt} UTC");
        }

        foreach (var row in status.NotShipped)
            output.WriteLine($"not-loaded {row.Key}  {row.State} {row.RecordedAt} UTC, and this build does not ship it");

        WriteChanged(status.In(MigrationState.Changed), output);

        var pending = status.In(MigrationState.Pending).Count;
        var notNeeded = status.In(MigrationState.NotNeeded).Count;
        if (pending + notNeeded > 0)
        {
            output.WriteLine(
                $"{pending} pending, {notNeeded} not needed and not yet recorded. Run db-migrate.");
        }

        return status.Current ? 0 : 1;
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
