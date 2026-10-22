using Marten;
using barakoCMS.Models;

namespace BarakoCMS.Tests;

/// <summary>
/// The content type name index ignores case (#1158), so a migrated database cannot hold two types
/// that differ only by case. Only data from before that index can. A test that needs both spellings
/// drops the index for its run and puts it back from its own definition, after the rows that break
/// it are gone.
/// </summary>
internal sealed class LegacyNameIndex : IAsyncDisposable
{
    private const string IndexName = "mt_doc_contenttypedefinition_uidx_name";

    private readonly IDocumentStore _store;
    private readonly string _restore;
    private readonly Func<Task> _cleanup;

    private LegacyNameIndex(IDocumentStore store, string restore, Func<Task> cleanup)
    {
        _store = store;
        _restore = restore;
        _cleanup = cleanup;
    }

    /// <summary>Drops the index. <paramref name="cleanup"/> runs on dispose, before the index is put back.</summary>
    public static async Task<LegacyNameIndex> DropAsync(IDocumentStore store, Func<Task> cleanup, CancellationToken ct)
    {
        // Marten checks a type's schema on first use. Do it now, or the first seed puts the index back.
        await store.Storage.Database.EnsureStorageExistsAsync(typeof(ContentTypeDefinition), ct);
        await using var connection = store.Storage.Database.CreateConnection();
        await connection.OpenAsync(ct);
        await using var read = connection.CreateCommand();
        read.CommandText = $"select indexdef from pg_indexes where indexname = '{IndexName}'";
        var definition = (string?)await read.ExecuteScalarAsync(ct);
        if (definition is null)
        {
            return new LegacyNameIndex(store, "select 1", cleanup);
        }

        await RunAsync(store, $"drop index public.{IndexName}", ct);
        return new LegacyNameIndex(store, definition.Replace("INDEX " + IndexName, "INDEX IF NOT EXISTS " + IndexName), cleanup);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _cleanup();
        }
        finally
        {
            await RunAsync(_store, _restore, CancellationToken.None);
        }
    }

    // A plain connection, not a session's: a session runs its commands in a transaction it never commits here.
    private static async Task RunAsync(IDocumentStore store, string sql, CancellationToken ct)
    {
        await using var connection = store.Storage.Database.CreateConnection();
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
    }
}
