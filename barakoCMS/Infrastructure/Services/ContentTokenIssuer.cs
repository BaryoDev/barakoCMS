using barakoCMS.Core.Validation;
using barakoCMS.Events;
using barakoCMS.Models;
using Marten;

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
/// Uniqueness rests on the randomness and is not looked up. A lookup could not use an index (the
/// field is a key inside the entry's JSON), so it was a scan of every entry of the type per token,
/// and a bulk import of N rows into a type of M entries was N scans of M rows, to guard against an
/// event that does not happen: among n tokens of L characters the chance that any two match is
/// below n squared over 2 to the power 5L + 1. At the shortest length (16, 80 bits) and a million
/// entries that is about 4 in 10 to the 13. The tokens one writer issues are still kept apart,
/// which costs nothing.
///
/// A token is never logged.
/// </remarks>
internal sealed class ContentTokenIssuer(IDocumentSession session)
{
    private readonly Dictionary<string, IReadOnlyList<FieldDefinition>> _fields = new(StringComparer.Ordinal);
    private readonly HashSet<string> _issued = new(StringComparer.Ordinal);

    /// <summary>Gives a new entry its tokens, discarding whatever the event carried under those fields.</summary>
    public async Task IssueAsync(ContentCreated created, CancellationToken ct)
    {
        foreach (var field in await FieldsAsync(created.ContentType, ct))
        {
            RemoveKeys(created.Data, field.Name);
            created.Data[field.Name] = NewToken(field);
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

            if (storedKey is not null && TokenFields.IsWellFormed(stored))
                updated.Data[storedKey] = stored!;
            else
                updated.Data[field.Name] = NewToken(field);
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

    private string NewToken(FieldDefinition field)
    {
        string token;
        do
        {
            token = TokenFields.Generate(TokenFields.LengthOf(field));
        }
        while (!_issued.Add(token));

        return token;
    }

    private static void RemoveKeys(Dictionary<string, object> data, string name)
    {
        foreach (var key in data.Keys.Where(k => Matches(k, name)).ToList())
            data.Remove(key);
    }

    private static bool Matches(string key, string name) =>
        string.Equals(key, name, StringComparison.OrdinalIgnoreCase);
}
