using barakoCMS.Models;
using Marten;

namespace BarakoCMS.Diagnostics.Redaction;

/// <summary>The names of content fields marked Sensitive or Hidden, whose values the redactor removes.</summary>
internal static class SensitiveFieldNames
{
    public static async Task<IReadOnlyCollection<string>> LoadAsync(IQuerySession session, bool allTenants, CancellationToken ct)
    {
        var types = allTenants
            ? await session.Query<ContentTypeDefinition>().Where(t => t.AnyTenant()).ToListAsync(ct)
            : await session.Query<ContentTypeDefinition>().ToListAsync(ct);

        // Account-owned types are global documents, so one query covers every tenant either way.
        var owned = await session.Query<AccountContentType>().ToListAsync(ct);

        return types
            .Concat(owned.Select(o => o.Definition))
            .SelectMany(t => t.Fields)
            .Where(f => f.Sensitivity != SensitivityLevel.Public)
            .Select(f => f.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
