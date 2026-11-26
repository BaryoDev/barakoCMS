using JasperFx;
using Marten;
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
            await database.Migrator.ApplyAllAsync(
                connection, new SchemaMigration(creates), AutoCreate.CreateOnly, new QuietLogger(), ct);

            names.AddRange(creates.Select(d => prefix + d.SchemaObject.Identifier.QualifiedName));
        }

        return names;
    }

    private static List<ISchemaObjectDelta> CreateDeltas(SchemaMigration migration) =>
        migration.Deltas.Where(d => d.Difference == SchemaPatchDifference.Create).ToList();

    /// <summary>The command prints what it created itself, so Weasel's own SQL echo stays off.</summary>
    private sealed class QuietLogger : IMigrationLogger
    {
        public void SchemaChange(string sql)
        {
        }

        public void OnFailure(System.Data.Common.DbCommand command, Exception ex)
        {
        }
    }
}
