using Marten;
using barakoCMS.Models;

namespace barakoCMS.Infrastructure.Services;

/// <summary>
/// Writes <see cref="User.NormalizedUsername"/> and <see cref="User.NormalizedEmail"/> into any user
/// document that does not carry them yet, and refuses to start when two accounts collide.
/// </summary>
/// <remarks>
/// <para>
/// Every lookup by username or email reads those fields, so an account stored before they existed
/// is invisible to sign-in until they are written. Production gets them from
/// <c>migrations/4.2.0/user-normalized-identity.sql</c>. A Development database runs
/// AutoCreate.CreateOrUpdate, which adds the new indexes without that file, so this is what brings
/// its existing accounts along. It also covers a value Postgres and .NET lowercase differently.
/// </para>
/// <para>
/// A collision is refused rather than resolved, for the same reason the migration refuses: which of
/// two accounts to keep is a decision about people, not something to pick at start-up. See #638.
/// </para>
/// </remarks>
internal static class UserIdentityBackfill
{
    public static async Task RunAsync(IDocumentStore store, CancellationToken ct = default)
    {
        await using var session = store.LightweightSession();

        var stale = await session.Query<User>()
            .AnyAsync(u => u.NormalizedUsername == null || u.NormalizedEmail == null, ct);
        if (!stale)
        {
            return;
        }

        var users = await session.Query<User>().ToListAsync(ct);

        var collisions = Collisions(users, u => u.NormalizedUsername, "username")
            .Concat(Collisions(users, u => u.NormalizedEmail, "email"))
            .ToList();
        if (collisions.Count > 0)
        {
            throw new InvalidOperationException(
                "Refusing to start: some accounts share a username or email once case and surrounding "
              + "spaces are ignored, and sign-in could not tell them apart. Nothing was changed. Rename or "
              + "remove one account in each group, then start again.\n"
              + string.Join("\n", collisions));
        }

        var table = $"{store.Options.DatabaseSchemaName}.mt_doc_users";
        foreach (var user in users)
        {
            // A patch of two fields rather than Store(user): another node can be serving while this
            // one starts, and rewriting the whole document would put back a lockout counter or a
            // password it changed a moment ago.
            session.QueueSqlCommand(
                $"update {table} set data = data || jsonb_build_object('NormalizedUsername', ?::text, 'NormalizedEmail', ?::text) where id = ?",
                user.NormalizedUsername, user.NormalizedEmail, user.Id);
        }

        await session.SaveChangesAsync(ct);
    }

    private static IEnumerable<string> Collisions(IReadOnlyList<User> users, Func<User, string> key, string kind) =>
        users.GroupBy(key)
            .Where(g => g.Count() > 1)
            .Select(g => $"{kind} '{g.Key}' is held by "
                       + string.Join(", ", g.OrderBy(u => u.Id).Select(u => $"{u.Id} ({u.Username})")));
}
