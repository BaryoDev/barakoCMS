using JasperFx;
using Marten;
using Npgsql;
using Weasel.Core;
using Weasel.Core.Migrations;

namespace barakoCMS.Infrastructure.Migrations;

/// <summary>
/// The tables, functions and other schema objects the loaded modules declare that the database
/// does not have at all, found and created without touching anything that exists.
/// </summary>
/// <remarks>
/// A release that adds a document type adds a table. The API creates it on start under
/// <c>AutoCreate.CreateOnly</c>, but <c>db-assert</c> runs before the API starts, so it reported
/// every new table as drift and the upgrade notes said not to start. This is the same creation,
/// done by <c>db-migrate</c>. Only deltas Marten classifies as <see cref="SchemaPatchDifference.Create"/>
/// are applied. A table that exists with an extra or missing index, or a changed column, is an
/// update delta, so it is left as it is and <c>db-assert</c> still fails on it.
/// </remarks>
internal static class MissingSchemaObjects
{
    /// <summary>Qualified names of the objects that would be created, across every database the store knows.</summary>
    public static async Task<IReadOnlyList<string>> PendingAsync(IDocumentStore store, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);

        var names = new List<string>();
        var databases = await store.Storage.AllDatabases();
        foreach (var database in databases)
        {
#pragma warning disable CS0618 // Identifier is what the module schema preflight names a database by
            var prefix = databases.Count > 1 ? $"{database.Identifier}: " : string.Empty;
#pragma warning restore CS0618
            var migration = await database.CreateMigrationAsync(ct);
            names.AddRange(CreateDeltas(migration).Select(d => prefix + d.SchemaObject.Identifier.QualifiedName));
        }

        return names;
    }

    /// <summary>Creates every missing object and returns their qualified names. Existing objects are not read for change and not altered.</summary>
    public static async Task<IReadOnlyList<string>> CreateAsync(IDocumentStore store, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);

        var names = new List<string>();
        var databases = await store.Storage.AllDatabases();
        foreach (var database in databases)
        {
#pragma warning disable CS0618 // Identifier is what the module schema preflight names a database by
            var prefix = databases.Count > 1 ? $"{database.Identifier}: " : string.Empty;
#pragma warning restore CS0618
            var migration = await database.CreateMigrationAsync(ct);
            var creates = CreateDeltas(migration);
            if (creates.Count == 0)
            {
                continue;
            }

            await using var connection = database.CreateConnection();
            await connection.OpenAsync(ct);
            // Weasel rethrows a failed statement only for its own logger. Any other logger is told and
            // the run goes on, so a create the database refused would be printed as created. The
            // writer is null because the command prints what it created itself.
            await database.Migrator.ApplyAllAsync(
                connection, new SchemaMigration(creates), AutoCreate.CreateOnly, new DefaultMigrationLogger(TextWriter.Null), ct);

            names.AddRange(creates.Select(d => prefix + d.SchemaObject.Identifier.QualifiedName));
        }

        return names;
    }

    /// <summary>
    /// What <c>db-migrate</c> does after the ledger run, so a file that changes an existing table has run before the tables the
    /// build adds are created. Creates only what is missing: an existing table is never altered, so
    /// drift on one still fails <c>db-assert</c>.
    /// </summary>
    public static async Task<int> CreateAndReportAsync(
        IDocumentStore store, IConfiguration? configuration, TextWriter output, CancellationToken ct)
    {
        IReadOnlyList<string> created = [];

        try
        {
            // The lock a starting host takes for the same work, so a host that starts meanwhile and this
            // command do not both create the same table.
            await Services.SchemaApplyLock.RunAsync(
                store, async () => created = await CreateAsync(store, ct), configuration, ct);
        }
        catch (Exception ex) when (ex is OperationCanceledException or NpgsqlException or TimeoutException
                                       or InsufficientDatabasePrivilegeException)
        {
            output.WriteLine(ct.IsCancellationRequested
                ? "cancelled  while creating missing schema objects."
                : $"failed     creating missing schema objects  {MigrationLedger.Describe(ex)}");
            output.WriteLine(
                "The migrations listed above stay in place with their rows. The next run creates what is still missing.");
            return 1;
        }

        foreach (var name in created)
        {
            output.WriteLine($"created    {name}");
        }

        output.WriteLine($"{created.Count} missing schema object(s) created.");
        return 0;
    }

    private static List<ISchemaObjectDelta> CreateDeltas(SchemaMigration migration) =>
        migration.Deltas.Where(d => d.Difference == SchemaPatchDifference.Create).ToList();
}
