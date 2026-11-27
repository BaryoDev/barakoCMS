using System.Security.Claims;
using barakoCMS.Core.Interfaces;
using barakoCMS.Core.Validation;
using barakoCMS.Infrastructure.Jobs;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using Marten;

namespace barakoCMS.Infrastructure.Connectors;

/// <summary>
/// The fields a connector may send on top of the Public ones, as <c>Type.Field</c> entries.
/// </summary>
/// <remarks>
/// A grant decides what leaves the system for a third party, so every question here answers no when
/// something is missing or cannot be read. Entries name a top level field of a content type. Both
/// parts are compared ignoring case. A Hidden field and a field its owner can hide are never
/// granted: the first has no reader, and the second is hidden per entry by a rule a grant on the
/// type cannot see.
/// </remarks>
internal static class ConnectorGrants
{
    /// <summary>The most entries one connector may list.</summary>
    public const int MaxEntries = 200;

    public static string Key(string type, string field) => type + "." + field;

    /// <summary>The entries as a set that compares the way a grant does, ignoring case.</summary>
    public static HashSet<string> SetOf(IEnumerable<string>? entries) =>
        new(entries ?? [], StringComparer.OrdinalIgnoreCase);

    /// <summary>Splits <c>Type.Field</c>. One dot, both parts present, no space around them and no control characters.</summary>
    public static bool TryParse(string? entry, out string type, out string field)
    {
        type = field = string.Empty;
        if (string.IsNullOrEmpty(entry) || entry.Length > 200 || entry.Any(char.IsControl)) return false;

        var parts = entry.Split('.');
        if (parts.Length != 2 || parts.Any(p => p.Length == 0 || p != p.Trim())) return false;

        (type, field) = (parts[0], parts[1]);
        return true;
    }

    /// <summary>
    /// Whether this field of <paramref name="type"/> may leave: Public and not owner-hidden, or
    /// Sensitive or Members and granted on this type. Hidden and owner-hidden fields never do.
    /// </summary>
    public static bool Passes(ISet<string>? grants, string type, FieldDefinition field)
    {
        if (field.Sensitivity == SensitivityLevel.Hidden || PublicFields.IsOwnerHidden(field)) return false;
        if (field.Sensitivity == SensitivityLevel.Public) return true;

        return grants is not null && grants.Contains(Key(type, field.Name));
    }

    /// <summary>The first hole that reads a field that may not leave, or null.</summary>
    public static (string Hole, FieldDefinition Field)? FirstRefused(
        IEnumerable<string?> templates, ContentTypeDefinition schema, ISet<string>? grants)
    {
        foreach (var template in templates)
        {
            foreach (var hole in TemplateHoles.Scan(template))
            {
                foreach (var read in hole.Reads)
                {
                    if (TemplateHoles.FieldRead(read, schema) is { } field && !Passes(grants, schema.Name, field))
                    {
                        return (hole.Text, field);
                    }
                }
            }
        }

        return null;
    }

    /// <summary>The fields of <paramref name="schema"/> a template reads that are not Public and are granted.</summary>
    public static HashSet<string> GrantedReads(string? template, ContentTypeDefinition schema, ISet<string>? grants)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (grants is null || grants.Count == 0) return found;

        foreach (var hole in TemplateHoles.Scan(template))
        {
            foreach (var read in hole.Reads)
            {
                if (TemplateHoles.FieldRead(read, schema) is { } field
                    && field.Sensitivity != SensitivityLevel.Public && Passes(grants, schema.Name, field))
                {
                    found.Add(field.Name);
                }
            }
        }

        return found;
    }

    /// <summary>
    /// Whether a query projects, filters or sorts on a field that is not Public, or follows a
    /// reference, which reads a field of another type. With grants, those are the fields a grant opens.
    /// </summary>
    public static bool QueryReadsNonPublic(QueryDefinition query, ContentTypeDefinition schema) =>
        NamedFields(query).Any(n => n is not null && (n.Contains('.') || schema.Fields.Any(f =>
                string.Equals(f.Name, n, StringComparison.OrdinalIgnoreCase) && f.Sensitivity != SensitivityLevel.Public)));

    /// <summary>Every field name a query projects, filters or sorts on. Nulls are skipped.</summary>
    public static IEnumerable<string> NamedFields(QueryDefinition query) =>
        (query.Fields ?? []).Where(n => n is not null)
            .Concat((query.Filters ?? []).Where(f => f is not null && f.Field is not null).Select(f => f.Field))
            .Concat(query.SortField is null ? [] : [query.SortField]);

    /// <summary>The names of the connectors of the session's tenant whose grants name <paramref name="type"/>, and the field when one is given.</summary>
    public static async Task<List<string>> NamingAsync(IQuerySession session, string type, string? field, CancellationToken ct)
    {
        var connectors = await session.Query<Connector>().ToListAsync(ct);
        return connectors
            .Where(c => c.AllowedFields is { Count: > 0 } && c.AllowedFields.Any(e => Names(e, type, field)))
            .Select(c => c.Slug)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The connectors, as <c>slug (tenant)</c>, of each of these tenants whose grants name
    /// <paramref name="type"/>. Each tenant is read on a session opened for it.
    /// </summary>
    public static async Task<List<string>> NamingInTenantsAsync(
        IDocumentStore store, IEnumerable<string> tenantIds, Func<string, string> slugOf, string type, CancellationToken ct)
    {
        var found = new List<string>();
        foreach (var id in tenantIds.Distinct(StringComparer.Ordinal))
        {
            await using var tenantSession = store.QuerySession(id);
            found.AddRange((await NamingAsync(tenantSession, type, null, ct)).Select(slug => $"{slug} ({slugOf(id)})"));
        }

        return found;
    }

    private static bool Names(string entry, string type, string? field) =>
        TryParse(entry, out var t, out var f)
        && string.Equals(t, type, StringComparison.OrdinalIgnoreCase)
        && (field is null || string.Equals(f, field, StringComparison.OrdinalIgnoreCase));

    /// <summary>The refusal for removing or renaming what a connector's grant names.</summary>
    public static string Refusal(string what, IReadOnlyCollection<string> connectors) =>
        $"{what} is granted to the connector {string.Join(", ", connectors.Select(c => $"'{c}'"))}. "
        + "Remove the grant from the connector first.";

    /// <summary>The list as a line for an audit row: names only, in order.</summary>
    public static string Line(IEnumerable<string>? entries) => string.Join(", ", entries ?? []);
}

/// <summary>What a save leaves as a connector's grant, or why it cannot be saved.</summary>
internal sealed record GrantOutcome(List<string>? Entries, string? Refusal, bool Forbidden = false, bool Busy = false);

/// <summary>Decides a connector's grant on a save: validates what a request adds and keeps what it does not touch.</summary>
internal static class ConnectorGrantRules
{
    /// <summary>
    /// The grant a connector holds after a save.
    /// </summary>
    /// <param name="requested">The list the save sent. Null is absent and keeps the stored list, empty clears it.</param>
    /// <param name="stored">The list the connector holds now.</param>
    /// <param name="moved">Whether the base URL changed. A move clears the stored list, so every entry sent with it is new.</param>
    /// <param name="who">
    /// A signed-in caller needs read rights on each entry added. The host, which applies a file it
    /// was configured with, skips that one rule and no other.
    /// </param>
    /// <param name="writer">
    /// The session a save writes through. When given, the field change lock is taken on each type an
    /// entry adds, as the block bindings route takes it, and a change running on one refuses the save.
    /// A plan passes none: it writes nothing.
    /// </param>
    public static async Task<GrantOutcome> NextAsync(
        IQuerySession session, ISensitivityService? sensitivity, ConfigCaller who,
        List<string>? requested, List<string>? stored, bool moved, CancellationToken ct, IDocumentSession? writer = null)
    {
        var kept = moved ? [] : stored ?? [];
        if (requested is null) return new(kept.Count > 0 ? [.. kept] : null, null);
        if (requested.Count == 0) return new(null, null);

        if (requested.Count > ConnectorGrants.MaxEntries)
        {
            return Refused($"allowedFields holds at most {ConnectorGrants.MaxEntries} entries.");
        }

        var held = ConnectorGrants.SetOf(kept);
        if (writer is not null && await BusyTypeAsync(writer, requested, held, ct) is { } busy)
        {
            return new(null, FieldChanges.Busy(busy), Busy: true);
        }

        var next = new List<string>();
        var seen = ConnectorGrants.SetOf(null);
        foreach (var raw in requested)
        {
            if (!ConnectorGrants.TryParse(raw, out var type, out var field))
            {
                return Refused($"'{Show(raw)}' is not a grant. Write one as Type.Field, naming a top level field.");
            }

            var entry = held.TryGetValue(ConnectorGrants.Key(type, field), out var same)
                ? Checked.Ok(same)
                : await CheckAddedAsync(session, sensitivity, who, type, field, ct);
            if (entry.Refusal is not null) return new(null, entry.Refusal, entry.Forbidden);

            if (seen.Add(entry.Entry!)) next.Add(entry.Entry!);
        }

        return new(next, null);
    }

    private static GrantOutcome Refused(string reason) => new(null, reason);

    /// <summary>
    /// Locks each type the save adds an entry for, lowest lowered name first, and returns the field
    /// change running on one of them. Taken before the type is read by the check, so a change that
    /// finished in between is seen and one that is running is not missed.
    /// </summary>
    private static async Task<FieldChange?> BusyTypeAsync(
        IDocumentSession writer, List<string> requested, ISet<string> held, CancellationToken ct)
    {
        var types = requested
            .Where(raw => ConnectorGrants.TryParse(raw, out _, out _))
            .Select(raw => { ConnectorGrants.TryParse(raw, out var type, out var field); return (Type: type, Key: ConnectorGrants.Key(type, field)); })
            .Where(e => !held.Contains(e.Key))
            .Select(e => e.Type.ToLower())
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();

        foreach (var type in types)
        {
            await FieldChanges.LockTypeAsync(writer, type, ct);
            if (await FieldChanges.RunningAsync(writer, type, ct) is { } running) return running;
        }

        return null;
    }

    private static string Show(string? raw) =>
        raw is { Length: <= 100 } && !raw.Any(char.IsControl) ? raw : "(not a valid grant)";

    private static async Task<Checked> CheckAddedAsync(
        IQuerySession session, ISensitivityService? sensitivity, ConfigCaller who, string type, string field, CancellationToken ct)
    {
        var named = $"{type}.{field}";
        var definition = await AccountContentTypes.ResolveAsync(session, type, ct);
        if (definition is null) return Checked.Not($"'{named}' names a content type that does not exist.");

        var declared = definition.Fields.FirstOrDefault(f => string.Equals(f.Name, field, StringComparison.OrdinalIgnoreCase));
        if (declared is null) return Checked.Not($"'{named}' names a field the content type does not have.");

        var entry = ConnectorGrants.Key(definition.Name, declared.Name);
        if (declared.Sensitivity == SensitivityLevel.Hidden)
        {
            return Checked.Not($"'{entry}' is Hidden, and a Hidden field cannot be granted.");
        }

        if (PublicFields.IsOwnerHidden(declared))
        {
            return Checked.Not($"'{entry}' can be hidden by its owner, and a field like that cannot be granted.");
        }

        // The host, applying a file it was configured with, has no user to ask. Everything above
        // still applied to it.
        if (who.IsSystem) return Checked.Ok(entry);

        if (sensitivity is null || who.Principal is not ClaimsPrincipal principal
            || !await sensitivity.MayReadFieldAsync(definition.Name, declared, null, principal, ct))
        {
            return Checked.Not($"You may not read '{entry}', so you may not grant it.", forbidden: true);
        }

        return Checked.Ok(entry);
    }

    private readonly record struct Checked(string? Entry, string? Refusal, bool Forbidden = false)
    {
        public static Checked Ok(string entry) => new(entry, null);

        public static Checked Not(string reason, bool forbidden = false) => new(null, reason, forbidden);
    }
}
