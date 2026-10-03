using System.Globalization;
using System.Text;
using System.Text.Json;
using barakoCMS.Core.Interfaces;
using barakoCMS.Core.Validation;
using barakoCMS.Models;
using Marten;
using Marten.Linq.MatchesSql;
using Marten.Services;
using LogSafe = barakoCMS.Infrastructure.Logging.LogSafe;

namespace barakoCMS.Infrastructure.Services;

/// <summary>
/// Refuses an entry write that would leave two entries holding the values a uniqueness rule of the
/// type allows one entry to hold.
/// </summary>
/// <remarks>
/// <para>
/// What makes it hold is a PostgreSQL advisory lock, taken in the transaction that writes the entry
/// and keyed on the tenant, the type, the rule and a hash of the values. Without it two writes at
/// once each read that no entry holds the value and both commit. With it the second waits for the
/// first to commit or roll back and then reads what the first left. The lock ends with the
/// transaction, so nothing is left behind by a crash and there is nothing to release when an entry
/// leaves the state, changes its value or is erased: the entries themselves are what is read.
/// </para>
/// <para>
/// The lock is tried rather than waited on, again and again until <see cref="LockWait"/> runs out.
/// A waiting lock has no bound of its own, and inside an import the holder's transaction lasts until
/// the whole import commits. A lock that never waits in the database also cannot deadlock: two
/// imports taking the same values in opposite orders each give up on the row the other holds,
/// instead of one of them failing with a deadlock.
/// </para>
/// <para>
/// Not a unique index, because every type of every tenant shares one table and an index per rule
/// would be a schema change made by configuration at run time. Not a document per held value
/// either: that is a second record of who holds what, which a restore, a rebuild or a write made
/// around the writer would leave out of step with the entries.
/// </para>
/// <para>
/// One comparison, and PostgreSQL makes it. The lock key is <c>jsonb_hash_extended</c> of the
/// values and the check is <c>jsonb</c> equality on them, so two values that are equal to the check
/// take the same lock: text exactly, numbers by value, text never equal to a number. The values go
/// to both as a bound parameter. An id field (uuid or reference) is lowered and stripped of braces,
/// parentheses and dashes on both sides first, since the entry validator stores an id as written.
/// </para>
/// <para>
/// An entry that already held its values before this write is not refused over another entry
/// holding them too. That is how a type that had duplicates when the rule was declared keeps
/// taking edits to those entries. The stored entry is read under the lock to decide it, not taken
/// from the caller's copy.
/// </para>
/// </remarks>
internal sealed class ContentUniqueness(IDocumentSession session, ILogger? logger = null)
{
    private const string TryLockSql =
        "select pg_try_advisory_xact_lock(hashtextextended(?, 0) # jsonb_hash_extended(?::jsonb, 0))";

    /// <summary>How long a write tries for the lock before it is refused as in progress elsewhere.</summary>
    public static readonly TimeSpan LockWait = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan LongestPause = TimeSpan.FromMilliseconds(200);

    private const string NoCreator = "00000000-0000-0000-0000-000000000000";

    private readonly Dictionary<string, TypeRules?> _types = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The values each entry written through this session since its last commit holds, by rule. Those
    /// writes are not stored yet, so a query cannot answer for them. Forgotten on every commit, after
    /// which the database answers again and another request may have moved any of them.
    /// </summary>
    private readonly Dictionary<Guid, Dictionary<string, string>> _held = new();

    private bool _listening;

    private sealed record TypeRules(string Name, IReadOnlyList<UniquenessRules.Usable> Rules);

    /// <param name="Json">The values as a JSON array, for the lock and the query.</param>
    /// <param name="Canonical">The same values in a form two equal keys share, for this session's own writes.</param>
    private sealed record EntryKey(string Json, string Canonical);

    /// <summary>
    /// Checks <paramref name="entry"/>, as the write will leave it, against every rule of its type.
    /// </summary>
    /// <exception cref="ContentUniquenessException">Another entry holds the values of one rule.</exception>
    public async Task EnforceAsync(Content entry, CancellationToken ct)
    {
        var type = await TypeAsync(entry.ContentType, ct);
        if (type is null)
        {
            return;
        }

        var holds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rule in type.Rules)
        {
            if (KeyOf(rule, entry) is not { } key)
            {
                continue;
            }

            await session.BeginTransactionAsync(ct);
            await LockAsync(type, rule, key, ct);

            var heldHere = _held.Any(other =>
                other.Key != entry.Id
                && other.Value.TryGetValue(rule.Name, out var theirs)
                && string.Equals(theirs, key.Canonical, StringComparison.Ordinal));

            // The exemption applies to both: an entry that held these values before this write is
            // not refused over another holding them too, whether that other is stored or was
            // written earlier through this session. A sweep or a rebuild that rewrites two entries
            // a forced rule left sharing a value then writes both. For an entry this session already
            // wrote, what it held is that write, not the stored row the write has not reached yet.
            if ((heldHere || await HeldByAnotherAsync(type, rule, key, entry.Id, ct))
                && !await HeldBeforeAsync(rule, key, entry.Id, ct))
            {
                throw new ContentUniquenessException(type.Name, rule.Name, rule.WhenState);
            }

            holds[rule.Name] = key.Canonical;
        }

        ListenForCommits();
        _held[entry.Id] = holds;
    }

    private async Task<bool> HeldBeforeAsync(UniquenessRules.Usable rule, EntryKey key, Guid entryId, CancellationToken ct) =>
        _held.TryGetValue(entryId, out var written)
            ? written.TryGetValue(rule.Name, out var was) && string.Equals(was, key.Canonical, StringComparison.Ordinal)
            : await HeldAlreadyAsync(rule, key, entryId, ct);

    /// <summary>Tries for the lock until it is taken or <see cref="LockWait"/> runs out.</summary>
    /// <remarks>
    /// The lock is the transaction's, so a lock this session already holds is taken again at once,
    /// which is what lets one import write several entries with one value under a rule that has no
    /// state of its own to fail them.
    /// </remarks>
    private async Task LockAsync(TypeRules type, UniquenessRules.Usable rule, EntryKey key, CancellationToken ct)
    {
        var scope = LockScope(type.Name, rule.Name);
        var deadline = DateTime.UtcNow + LockWait;
        var pause = TimeSpan.FromMilliseconds(20);

        while (true)
        {
            var taken = await session.QueryAsync<bool>(TryLockSql, ct, scope, key.Json);
            if (taken.Count > 0 && taken[0])
            {
                return;
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw ContentUniquenessException.InProgress(type.Name, rule.Name, rule.WhenState);
            }

            await Task.Delay(pause, ct);
            pause = pause * 2 < LongestPause ? pause * 2 : LongestPause;
        }
    }

    private void ListenForCommits()
    {
        if (_listening)
        {
            return;
        }

        _listening = true;
        session.Listeners.Add(new ForgetOnCommit(this));
    }

    private sealed class ForgetOnCommit(ContentUniqueness uniqueness) : DocumentSessionListenerBase
    {
        public override Task AfterCommitAsync(IDocumentSession session, IChangeSet commit, CancellationToken token)
        {
            uniqueness.Reset();
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Forgets what this session's writes hold: on a commit, after which they are stored, and once
    /// the session's pending changes are ejected, when none of them will be.
    /// </summary>
    public void Reset() => _held.Clear();

    /// <summary>
    /// The entries of a type that share their values under <paramref name="rule"/> with another
    /// entry the rule counts.
    /// </summary>
    /// <param name="schema">The database schema the document tables are in.</param>
    /// <remarks>
    /// The inner query names its tenant and its type as parameters and reads nothing from the outer
    /// row, so it is run once and not once per entry.
    /// </remarks>
    public static IQueryable<Content> Duplicates(
        IDocumentSession session, string schema, string contentType, UniquenessRules.Usable rule)
    {
        var lowered = contentType.ToLower();
        var fragment = new Fragment();

        fragment.Append("NOT EXISTS (SELECT 1 FROM jsonb_array_elements(");
        fragment.Key("d", rule);
        fragment.Append(") AS k(v) WHERE k.v = 'null'::jsonb OR k.v = '\"\"'::jsonb) AND ");
        fragment.State("d", rule);
        fragment.Key("d", rule);
        fragment.Append(" IN (SELECT ");
        fragment.Key("o", rule);
        fragment.Append($" FROM {schema}.mt_doc_contents o WHERE o.tenant_id = ?", session.TenantId);
        fragment.Append(" AND lower(o.data ->> 'ContentType') = ? AND ", lowered);
        fragment.State("o", rule);
        fragment.Append("true GROUP BY 1 HAVING count(*) > 1)");

        var sql = fragment.Sql;
        var parameters = fragment.Parameters;

        return session.Query<Content>()
            .Where(c => c.ContentType.ToLower() == lowered && c.MatchesSql(sql, parameters));
    }

    private async Task<TypeRules?> TypeAsync(string contentType, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return null;
        }

        if (_types.TryGetValue(contentType, out var known))
        {
            return known;
        }

        // Without regard to case, like the singleton cap: a rule a caller can walk past by typing
        // the type's name in capitals is not a rule.
        var lowered = contentType.ToLower();

        // A real session always answers a query; a test double built on a mock may hand back null,
        // and a type it cannot read declares no rule.
        var definitions = session.Query<ContentTypeDefinition>();
        var definition = definitions is null
            ? null
            : await definitions.FirstOrDefaultAsync(d => d.Name.ToLower() == lowered, ct);

        TypeRules? rules = null;

        if (definition?.Uniqueness is { Count: > 0 })
        {
            var resolved = UniquenessRules.Resolve(definition);

            if (resolved.Skipped.Count > 0)
            {
                logger?.LogWarning(
                    "Content type {ContentType} stores {Count} uniqueness rule(s) a save would refuse today, so they are not applied: {Rules}",
                    LogSafe.Value(definition.Name), resolved.Skipped.Count,
                    LogSafe.Value(string.Join(", ", resolved.Skipped)));
            }

            if (resolved.Usable.Count > 0)
            {
                rules = new TypeRules(definition.Name, resolved.Usable);
            }
        }

        _types[contentType] = rules;
        return rules;
    }

    private string LockScope(string type, string rule) =>
        $"barakocms:uniqueness:{session.TenantId}:{type.ToLowerInvariant()}:{rule.ToLowerInvariant()}";

    /// <summary>
    /// Whether an entry other than this one, and other than the ones this session wrote, is stored
    /// holding the key. This session's own writes are answered from memory by the caller.
    /// </summary>
    private async Task<bool> HeldByAnotherAsync(
        TypeRules type, UniquenessRules.Usable rule, EntryKey key, Guid entryId, CancellationToken ct)
    {
        var lowered = type.Name.ToLower();
        var fragment = Holder(rule, key);
        fragment.Append(" AND d.id <> ALL(?)", _held.Keys.Append(entryId).Distinct().ToArray());

        var sql = fragment.Sql;
        var parameters = fragment.Parameters;

        return await session.Query<Content>()
            .Where(c => c.ContentType.ToLower() == lowered && c.MatchesSql(sql, parameters))
            .AnyAsync(ct);
    }

    /// <summary>
    /// Whether this entry, as stored, already holds the key. A query and not a load, so a session
    /// that already holds the document cannot answer from the copy it is about to write.
    /// </summary>
    private async Task<bool> HeldAlreadyAsync(
        UniquenessRules.Usable rule, EntryKey key, Guid entryId, CancellationToken ct)
    {
        var fragment = Holder(rule, key);
        var sql = fragment.Sql;
        var parameters = fragment.Parameters;

        return await session.Query<Content>()
            .Where(c => c.Id == entryId && c.MatchesSql(sql, parameters))
            .AnyAsync(ct);
    }

    private static Fragment Holder(UniquenessRules.Usable rule, EntryKey key)
    {
        var fragment = new Fragment();
        fragment.State("d", rule);
        fragment.Key("d", rule);
        fragment.Append(" = ?::jsonb", key.Json);
        return fragment;
    }

    /// <summary>
    /// The entry's values under a rule, or null when the rule does not count the entry: it is in
    /// another state, or one of the fields holds nothing.
    /// </summary>
    private static EntryKey? KeyOf(UniquenessRules.Usable rule, Content entry)
    {
        if (rule.WhenState is { } state
            && !string.Equals(entry.LifecycleState ?? rule.InitialState, state, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var json = new List<string>(rule.Parts.Count);
        var canonical = new StringBuilder();

        foreach (var part in rule.Parts)
        {
            if (!TryRead(part, entry, out var element, out var comparable))
            {
                return null;
            }

            json.Add(element);
            canonical.Append(comparable.Length).Append(':').Append(comparable);
        }

        return new EntryKey($"[{string.Join(",", json)}]", canonical.ToString());
    }

    private static bool TryRead(UniquenessRules.Part part, Content entry, out string json, out string comparable)
    {
        json = comparable = string.Empty;

        if (part.Kind == UniquenessRules.CreatorKind)
        {
            if (entry.CreatedBy == Guid.Empty)
            {
                return false;
            }

            var creator = entry.CreatedBy.ToString("D");
            json = JsonSerializer.Serialize(creator);
            comparable = "s" + creator;
            return true;
        }

        var raw = ValueOf(entry.Data, part.Name) switch
        {
            null => null,
            JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => null,
            JsonElement element => element.GetRawText(),
            var value => JsonSerializer.Serialize(value, value.GetType()),
        };

        if (raw is null)
        {
            return false;
        }

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;

        switch (root.ValueKind)
        {
            case JsonValueKind.Null:
                return false;

            case JsonValueKind.String:
                var text = root.GetString() ?? string.Empty;
                if (part.Kind == UniquenessRules.IdKind)
                {
                    text = StripId(text);
                }
                else if (part.Kind == UniquenessRules.EmailKind)
                {
                    text = LowerAscii(text);
                }

                if (text.Length == 0)
                {
                    return false;
                }

                json = JsonSerializer.Serialize(text);
                comparable = "s" + text;
                return true;

            case JsonValueKind.Number:
                json = root.GetRawText();
                comparable = "n" + (decimal.TryParse(json, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                    ? number.ToString("G29", CultureInfo.InvariantCulture)
                    : json);
                return true;

            case JsonValueKind.True:
            case JsonValueKind.False:
                json = root.GetRawText();
                comparable = "b" + json;
                return true;

            default:
                json = root.GetRawText();
                comparable = "j" + json;
                return true;
        }
    }

    /// <summary>
    /// The value under the field's name without regard to case. Where an entry holds the name in
    /// two spellings, the one that sorts first by code point, which is the one <see cref="Fragment.Key"/>
    /// picks.
    /// </summary>
    private static object? ValueOf(Dictionary<string, object>? data, string field)
    {
        if (data is null)
        {
            return null;
        }

        string? found = null;
        foreach (var key in data.Keys)
        {
            if (string.Equals(key, field, StringComparison.OrdinalIgnoreCase)
                && (found is null || string.CompareOrdinal(key, found) < 0))
            {
                found = key;
            }
        }

        return found is null ? null : data[found];
    }

    private const string Upper = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";

    private const string Lower = "abcdefghijklmnopqrstuvwxyz";

    /// <summary>The C# side of <c>translate(value, Upper, Lower)</c>: A to Z lowered, nothing else.</summary>
    private static string LowerAscii(string value) =>
        string.Create(value.Length, value, (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                var c = source[i];
                span[i] = c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;
            }
        });

    /// <summary>The C# side of <c>translate(lower(value), '{}()-', '')</c>.</summary>
    private static string StripId(string value)
    {
        var stripped = new StringBuilder(value.Length);
        foreach (var c in value.ToLowerInvariant())
        {
            if (c is not ('{' or '}' or '(' or ')' or '-'))
            {
                stripped.Append(c);
            }
        }

        return stripped.ToString();
    }

    /// <summary>
    /// A <c>MatchesSql</c> fragment and its parameters, built together so they cannot fall out of
    /// order. <c>d</c> is the alias Marten gives the document table and <c>?</c> its placeholder.
    /// </summary>
    private sealed class Fragment
    {
        private readonly StringBuilder _sql = new();
        private readonly List<object> _parameters = new();

        public string Sql => _sql.ToString();

        public object[] Parameters => _parameters.ToArray();

        public void Append(string sql, params object[] parameters)
        {
            _sql.Append(sql);
            _parameters.AddRange(parameters);
        }

        /// <summary>
        /// The rule's state test on a row, ending in AND, or nothing for a rule that counts every
        /// entry. A row with no state is read as being in the type's initial state.
        /// </summary>
        public void State(string alias, UniquenessRules.Usable rule)
        {
            if (rule.WhenState is not { } state)
            {
                return;
            }

            Append(
                $"lower(coalesce({alias}.data ->> 'LifecycleState', ?)) = lower(?) AND ",
                rule.InitialState ?? string.Empty, state);
        }

        /// <summary>
        /// A row's values under the rule, as a jsonb array in the rule's field order with JSON null
        /// where the row holds nothing.
        /// </summary>
        public void Key(string alias, UniquenessRules.Usable rule)
        {
            var data = $"CASE WHEN jsonb_typeof({alias}.data -> 'Data') = 'object' THEN {alias}.data -> 'Data' ELSE '{{}}'::jsonb END";
            var value = $"FROM jsonb_each({data}) e WHERE lower(e.key) = lower(f.name) ORDER BY e.key COLLATE \"C\" LIMIT 1";

            _sql.Append("(SELECT jsonb_agg(COALESCE(CASE f.kind ")
                .Append($"WHEN '{UniquenessRules.CreatorKind}' THEN to_jsonb(NULLIF({alias}.data ->> 'CreatedBy', '{NoCreator}')) ")
                .Append($"WHEN '{UniquenessRules.IdKind}' THEN (SELECT CASE WHEN jsonb_typeof(e.value) = 'string' ")
                .Append("THEN to_jsonb(translate(lower(e.value #>> '{}'), '{}()-', '')) ELSE e.value END ")
                .Append(value)
                .Append($") WHEN '{UniquenessRules.EmailKind}' THEN (SELECT CASE WHEN jsonb_typeof(e.value) = 'string' ")
                .Append($"THEN to_jsonb(translate(e.value #>> '{{}}', '{Upper}', '{Lower}')) ELSE e.value END ")
                .Append(value)
                .Append(") ELSE (SELECT e.value ")
                .Append(value)
                .Append(") END, 'null'::jsonb) ORDER BY f.ord) ")
                .Append("FROM unnest(?::text[], ?::text[]) WITH ORDINALITY AS f(name, kind, ord))");

            _parameters.Add(rule.Parts.Select(p => p.Name).ToArray());
            _parameters.Add(rule.Parts.Select(p => p.Kind).ToArray());
        }
    }
}
