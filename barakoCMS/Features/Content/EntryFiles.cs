using System.Security.Claims;
using barakoCMS.Core.Interfaces;
using barakoCMS.Core.Validation;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using Marten;

namespace barakoCMS.Features.Content;

/// <summary>
/// The files an authoring read answers beside an entry's data, for the entry's <c>file</c> fields.
/// </summary>
/// <remarks>
/// <c>Data</c> keeps the id, because it is what a client sends back on the next save. The file each
/// id names goes in a member of its own, so a console can show a name and a thumbnail without a
/// request per field.
///
/// Run on data the sensitivity scrub has already been over, so a field masked from this caller
/// holds no id by the time it is read here. The file store is asked as the caller: a public file,
/// or a private one that is the caller's own or that the caller administers. Any other file has no
/// entry, as a deleted one has none.
/// </remarks>
internal static class EntryFiles
{
    /// <summary>
    /// For each entry, in order, the files its fields resolve to, or null when none does.
    /// </summary>
    /// <remarks>
    /// One query for the types on the page and one read of the file store for every
    /// <see cref="FileFieldResolver.IdsPerRead"/> distinct ids, whatever the number of entries.
    /// </remarks>
    public static async Task<IReadOnlyList<Dictionary<string, ResolvedFile>?>> ResolveAsync(
        IReadOnlyList<(string ContentType, Dictionary<string, object> Data)> entries,
        IQuerySession session,
        IFileStore? files,
        ClaimsPrincipal caller,
        CancellationToken ct)
    {
        var resolved = new Dictionary<string, ResolvedFile>?[entries.Count];
        if (entries.Count == 0 || files is null or NoFileStore)
            return resolved;

        var typeNames = entries.Select(e => e.ContentType).Distinct(StringComparer.Ordinal).ToArray();
        var definitions = await session.Query<ContentTypeDefinition>()
            .Where(d => d.Name.In(typeNames))
            .ToListAsync(ct);

        var fieldsOf = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var definition in definitions)
        {
            var names = FileFields.Names(definition);
            if (names.Count > 0)
                fieldsOf[definition.Name] = names;
        }

        if (fieldsOf.Count == 0)
            return resolved;

        var ids = new HashSet<Guid>();
        foreach (var (contentType, data) in entries)
        {
            if (fieldsOf.TryGetValue(contentType, out var fields))
                FileFieldResolver.Collect(data, fields, ids);
        }

        var found = await FileFieldResolver.ReadAsync(files, ids, caller, ct);
        if (found.Count == 0)
            return resolved;

        for (var i = 0; i < entries.Count; i++)
        {
            if (fieldsOf.TryGetValue(entries[i].ContentType, out var fields))
                resolved[i] = FileFieldResolver.Resolved(entries[i].Data, fields, found);
        }

        return resolved;
    }
}
