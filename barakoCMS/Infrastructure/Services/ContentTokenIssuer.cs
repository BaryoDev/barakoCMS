using barakoCMS.Core.Validation;
using barakoCMS.Events;
using barakoCMS.Features.Public;
using barakoCMS.Models;
using Marten;
using Marten.Linq.MatchesSql;

namespace barakoCMS.Infrastructure.Services;

/// <summary>
/// Fills and keeps the <c>token</c> fields of an entry, for <see cref="ContentWriter"/>.
/// </summary>
/// <remarks>
/// Here and not in an endpoint, because every route that writes an entry ends at the writer: the
/// create and update endpoints, an import, a push, a form, a sync, a workflow action, a transition
/// and a rollback. A rule kept in each of them is a rule one of them forgets.
///
/// On create the value is generated, whatever the event carried. On an update the stored value is
/// put back, whatever the event carried, and an entry that has none (it was written before its
/// type had the field) is given one.
///
/// A token is checked against the entries stored under the same type name in the session's tenant
/// before it is used, and against the ones this writer issued and has not committed. No unique
/// index backs that: two writers committing at the same moment are not checked against each other,
/// and at the shortest length the chance they collide is that of two 80 bit numbers matching.
///
/// A token is never logged and is not named in the one exception this throws.
/// </remarks>
internal sealed class ContentTokenIssuer(IDocumentSession session)
{
    /// <summary>How many tokens are tried for one field before the write fails.</summary>
    public const int MaxAttempts = 5;

    private readonly Dictionary<string, IReadOnlyList<FieldDefinition>> _fields = new(StringComparer.Ordinal);
    private readonly HashSet<string> _issued = new(StringComparer.Ordinal);

    /// <summary>Gives a new entry its tokens, discarding whatever the event carried under those fields.</summary>
    public async Task IssueAsync(ContentCreated created, CancellationToken ct)
    {
        foreach (var field in await FieldsAsync(created.ContentType, ct))
        {
            RemoveKeys(created.Data, field.Name);
            created.Data[field.Name] = await NewTokenAsync(created.ContentType, field, ct);
        }
    }

    /// <summary>
    /// Puts the entry's stored tokens into a data update, discarding whatever the event carried
    /// under those fields. An event that is not a data update is left alone.
    /// </summary>
    /// <param name="content">The entry as stored, before the event is applied to it.</param>
    public async Task KeepAsync(Content content, object @event, CancellationToken ct)
    {
        if (@event is not ContentUpdated updated)
            return;

        foreach (var field in await FieldsAsync(content.ContentType, ct))
        {
            // Read before anything is removed: a caller in process may hand over the entry's own
            // dictionary as the event's data.
            var storedKey = content.Data.Keys.FirstOrDefault(k => Matches(k, field.Name));
            var stored = storedKey is null ? null : content.Data[storedKey];

            RemoveKeys(updated.Data, field.Name);

            if (storedKey is not null && !TokenFields.IsBlank(stored))
                updated.Data[storedKey] = stored!;
            else
                updated.Data[field.Name] = await NewTokenAsync(content.ContentType, field, ct);
        }
    }

    /// <summary>The token fields of a type, read once per type for the life of this writer.</summary>
    /// <remarks>
    /// The name is matched ignoring case, as the singleton cap matches it, so an entry created as
    /// TICKET under a type named ticket still gets the type's tokens. A name with no definition is
    /// not remembered, because a later write in the same unit may follow the type's creation.
    /// </remarks>
    private async Task<IReadOnlyList<FieldDefinition>> FieldsAsync(string contentType, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(contentType))
            return [];

        if (_fields.TryGetValue(contentType, out var cached))
            return cached;

        var lowered = contentType.ToLower();
        var definitions = await session.Query<ContentTypeDefinition>()
            .Where(d => d.Name.ToLower() == lowered)
            .ToListAsync(ct);

        var definition = definitions.FirstOrDefault(d => d.Name == contentType) ?? definitions.FirstOrDefault();
        if (definition is null)
            return [];

        IReadOnlyList<FieldDefinition> fields = (definition.Fields ?? [])
            .Where(f => f is not null && TokenFields.IsToken(f.Type))
            .ToList();

        _fields[contentType] = fields;
        return fields;
    }

    private async Task<string> NewTokenAsync(string contentType, FieldDefinition field, CancellationToken ct)
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var token = TokenFields.Generate(TokenFields.LengthOf(field));

            if (!_issued.Add(token))
                continue;

            var (sql, parameters) = DeliveryQuery.FieldEqualsIgnoreCaseSql(field.Name, token);
            var held = await session.Query<Content>()
                .Where(c => c.ContentType == contentType && c.MatchesSql(sql, parameters))
                .AnyAsync(ct);

            if (!held)
                return token;
        }

        throw new InvalidOperationException(
            $"No unused token was found for field '{field.Name}' in {MaxAttempts} attempts, so the entry was not written.");
    }

    private static void RemoveKeys(Dictionary<string, object> data, string name)
    {
        foreach (var key in data.Keys.Where(k => Matches(k, name)).ToList())
            data.Remove(key);
    }

    private static bool Matches(string key, string name) =>
        string.Equals(key, name, StringComparison.OrdinalIgnoreCase);
}
