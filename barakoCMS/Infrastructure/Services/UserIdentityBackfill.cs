using Marten;
using Npgsql;
using Serilog;
using barakoCMS.Models;

namespace barakoCMS.Infrastructure.Services;

/// <summary>
/// Makes the stored <see cref="User.NormalizedUsername"/> and <see cref="User.NormalizedEmail"/> of
/// every account equal to what <see cref="User.NormalizeIdentity"/> computes, and refuses to start
/// when doing so would give two accounts the same value.
/// </summary>
/// <remarks>
/// <para>
/// Every lookup by username or email reads the stored fields, so a stored value that differs from
/// the computed one makes the account unreachable by its own name. Three things leave one wrong:
/// </para>
/// <list type="bullet">
/// <item>A Development database runs AutoCreate.CreateOrUpdate, which adds the indexes without the
/// migration, so an account stored before 4.2.0 has no normalised fields at all.</item>
/// <item><c>migrations/4.2.0/user-normalized-identity.sql</c> trims with <c>btrim</c>, which strips
/// spaces only, where .NET's <c>Trim</c> strips every whitespace character. <c>"alice\t"</c> is stored
/// as <c>"alice\t"</c>, so sign-in as <c>alice</c> misses it, and the migration's collision check lets
/// it sit beside an <c>"alice"</c> that .NET considers the same name.</item>
/// <item>The migration lowercases with <c>lower</c>, which follows the database's locale. Under
/// <c>lc_ctype C</c> it leaves <c>"Émile"</c> as it is.</item>
/// </list>
/// <para>
/// So this reads the stored values rather than trusting them, and it runs on every start. The scan
/// is keyset-paged and reads five text fields, and only the accounts that differ are held in memory.
/// </para>
/// <para>
/// A collision is refused rather than resolved, for the same reason the migration refuses: which of
/// two accounts to keep is a decision about people, not something to pick at start-up. See #638.
/// </para>
/// </remarks>
internal static class UserIdentityBackfill
{
    internal const int BatchSize = 1000;

    private sealed record Rewrite(Guid Id, string NormalizedUsername, string NormalizedEmail,
        bool UsernameChanges, bool EmailChanges);

    public static async Task RunAsync(IDocumentStore store, CancellationToken ct = default)
    {
        var table = $"{store.Options.DatabaseSchemaName}.mt_doc_users";

        await using var connection = store.Storage.Database.CreateConnection();
        await connection.OpenAsync(ct);

        var rewrites = await FindRewritesAsync(connection, table, ct);
        if (rewrites.Count == 0)
        {
            return;
        }

        var collisions = new List<string>();
        collisions.AddRange(await CollisionsAsync(connection, table, rewrites, "username", "NormalizedUsername",
            r => r.UsernameChanges, r => r.NormalizedUsername, ct));
        collisions.AddRange(await CollisionsAsync(connection, table, rewrites, "email", "NormalizedEmail",
            r => r.EmailChanges, r => r.NormalizedEmail, ct));

        if (collisions.Count > 0)
        {
            throw Refusal(string.Join("\n", collisions));
        }

        try
        {
            await WriteAsync(connection, table, rewrites, ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // Another node, still serving, stored an account on a value this was about to write, after
            // the check above ran. The transaction rolled back, and the next start names the accounts.
            throw Refusal("An account was stored on one of the rewritten values while this was starting. Start again to see which.", ex);
        }

        Log.Information("Rewrote the normalised username or email of {Count} account(s)", rewrites.Count);
    }

    /// <remarks>
    /// Account ids only. The values are usernames and addresses as registered, which may hold a line
    /// break, and this message goes to the fatal startup log.
    /// </remarks>
    private static InvalidOperationException Refusal(string detail, Exception? inner = null) =>
        new("Refusing to start: some accounts share a username or email once case and surrounding "
          + "whitespace are ignored, and sign-in could not tell them apart. Nothing was changed. Rename or "
          + "remove one account in each group, then start again.\n" + detail, inner);

    private static async Task<List<Rewrite>> FindRewritesAsync(NpgsqlConnection connection, string table, CancellationToken ct)
    {
        var rewrites = new List<Rewrite>();
        Guid? after = null;

        while (true)
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "select id, data ->> 'Username', data ->> 'Email', data ->> 'NormalizedUsername', data ->> 'NormalizedEmail' "
              + $"from {table} "
              + (after is null ? "" : "where id > @after ")
              + "order by id limit @limit";
            if (after is not null)
            {
                command.Parameters.AddWithValue("after", after.Value);
            }
            command.Parameters.AddWithValue("limit", BatchSize);

            var read = 0;
            await using (var reader = await command.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    read++;
                    var id = reader.GetGuid(0);
                    after = id;

                    var username = reader.IsDBNull(1) ? null : reader.GetString(1);
                    var email = reader.IsDBNull(2) ? null : reader.GetString(2);
                    var storedUsername = reader.IsDBNull(3) ? null : reader.GetString(3);
                    var storedEmail = reader.IsDBNull(4) ? null : reader.GetString(4);

                    var normalizedUsername = User.NormalizeIdentity(username);
                    var normalizedEmail = User.NormalizeIdentity(email);
                    var usernameChanges = !string.Equals(storedUsername, normalizedUsername, StringComparison.Ordinal);
                    var emailChanges = !string.Equals(storedEmail, normalizedEmail, StringComparison.Ordinal);

                    if (usernameChanges || emailChanges)
                    {
                        rewrites.Add(new Rewrite(id, normalizedUsername, normalizedEmail,
                            usernameChanges, emailChanges));
                    }
                }
            }

            if (read < BatchSize)
            {
                return rewrites;
            }
        }
    }

    /// <summary>
    /// Every group of accounts that would hold the same value once the rewrites are applied.
    /// </summary>
    /// <remarks>
    /// An account that is not rewritten keeps its stored value, and the unique index already keeps
    /// those apart, so a collision needs at least one rewritten account. It is either with another
    /// rewritten account, found in memory, or with an account that keeps its stored value, found
    /// through the index. An index hit on an account that is itself being rewritten is not one: that
    /// account ends up with its new value, which the in-memory grouping has already compared.
    /// </remarks>
    private static async Task<List<string>> CollisionsAsync(
        NpgsqlConnection connection, string table, IReadOnlyList<Rewrite> rewrites, string kind, string field,
        Func<Rewrite, bool> changes, Func<Rewrite, string> value, CancellationToken ct)
    {
        var changing = rewrites.Where(changes).ToList();
        var changingIds = changing.Select(r => r.Id).ToHashSet();

        var holders = changing
            .GroupBy(value, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(r => r.Id).ToList(), StringComparer.Ordinal);

        foreach (var chunk in holders.Keys.Chunk(BatchSize))
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                $"select id, data ->> '{field}' from {table} where data ->> '{field}' = any(@values)";
            command.Parameters.AddWithValue("values", chunk);

            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var id = reader.GetGuid(0);
                if (changingIds.Contains(id))
                {
                    continue;
                }

                holders[reader.GetString(1)].Add(id);
            }
        }

        return holders
            .Where(h => h.Value.Count > 1)
            .OrderBy(h => h.Key, StringComparer.Ordinal)
            .Select(h => $"one {kind} is held by accounts " + string.Join(", ", h.Value.OrderBy(id => id)))
            .ToList();
    }

    /// <summary>
    /// Applies the rewrites in one transaction, in two passes.
    /// </summary>
    /// <remarks>
    /// The first pass removes both fields from every rewritten account and the second writes the new
    /// values. Written in one pass, an account taking a value another rewritten account is about to
    /// give up would hit the unique index, which Postgres checks row by row. A missing field is null to
    /// the index, and nulls do not collide. Nobody else sees the gap, since it never commits.
    ///
    /// Patches of two fields rather than whole documents: another node can be serving while this one
    /// starts, and rewriting the whole document would put back a lockout counter or a password it
    /// changed a moment ago.
    /// </remarks>
    private static async Task WriteAsync(NpgsqlConnection connection, string table, IReadOnlyList<Rewrite> rewrites, CancellationToken ct)
    {
        await using var transaction = await connection.BeginTransactionAsync(ct);

        foreach (var chunk in rewrites.Chunk(BatchSize))
        {
            await using var strip = new NpgsqlCommand(
                $"update {table} set data = data - 'NormalizedUsername' - 'NormalizedEmail' where id = any(@ids)",
                connection, transaction);
            strip.Parameters.AddWithValue("ids", chunk.Select(r => r.Id).ToArray());
            await strip.ExecuteNonQueryAsync(ct);
        }

        foreach (var chunk in rewrites.Chunk(BatchSize))
        {
            await using var write = new NpgsqlCommand(
                $"update {table} as u set data = u.data || jsonb_build_object('NormalizedUsername', v.username, 'NormalizedEmail', v.email) "
              + "from unnest(@ids, @usernames, @emails) as v(id, username, email) where u.id = v.id",
                connection, transaction);
            write.Parameters.AddWithValue("ids", chunk.Select(r => r.Id).ToArray());
            write.Parameters.AddWithValue("usernames", chunk.Select(r => r.NormalizedUsername).ToArray());
            write.Parameters.AddWithValue("emails", chunk.Select(r => r.NormalizedEmail).ToArray());
            await write.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }
}
