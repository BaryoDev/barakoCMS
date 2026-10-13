using barakoCMS.Models;
using Marten;

namespace barakoCMS.Features.Blocks.Bindings;

/// <summary>The block bindings that name a field of a content type, by their sort or a condition.</summary>
/// <remarks>
/// A binding whose sort or condition names a field the type does not declare selects nothing, and
/// a page showing it empties with nothing said. So a rename carries every binding along, and a
/// remove is refused while one still names the field. Read through the caller's session, so only
/// the tenant that owns the type is reached.
/// </remarks>
internal static class BindingFields
{
    /// <summary>The bindings of <paramref name="contentType"/> whose sort or a condition names <paramref name="field"/>.</summary>
    public static async Task<List<BlockBinding>> NamingAsync(
        IQuerySession session, string contentType, string field, CancellationToken ct)
    {
        var lowered = contentType.ToLower();
        var bindings = await session.Query<BlockBinding>().Where(b => b.ContentType.ToLower() == lowered).ToListAsync(ct);
        return bindings.Where(b => Names(b, field)).OrderBy(b => b.Name, StringComparer.Ordinal).ThenBy(b => b.Id).ToList();
    }

    /// <summary>Renames <paramref name="from"/> to <paramref name="to"/> in every binding of the type that names it, staged on the session.</summary>
    public static async Task RenameAsync(IDocumentSession session, string contentType, string from, string to, CancellationToken ct)
    {
        foreach (var binding in await NamingAsync(session, contentType, from, ct))
        {
            if (binding.Sort is { } sort && Same(sort.TrimStart('-'), from))
            {
                binding.Sort = (sort.StartsWith('-') ? "-" : string.Empty) + to;
            }

            if (binding.Where is { } where)
            {
                var renamed = where.Where(kv => !Same(kv.Key, from)).ToDictionary(kv => kv.Key, kv => kv.Value);
                foreach (var (_, value) in where.Where(kv => Same(kv.Key, from)))
                {
                    renamed[to] = value;
                }

                binding.Where = renamed;
            }

            binding.UpdatedAt = DateTimeOffset.UtcNow;
            session.Store(binding);
        }
    }

    /// <summary>The refusal for removing a field while <paramref name="bindings"/> name it.</summary>
    public static string Refusal(string field, IReadOnlyCollection<BlockBinding> bindings) =>
        $"'{field}' is named by the block {(bindings.Count == 1 ? "binding" : "bindings")} "
        + string.Join(", ", bindings.Select(b => $"'{b.Name}' ({b.Id})"))
        + ", which would select nothing without it. Change or delete them first.";

    private static bool Names(BlockBinding binding, string field) =>
        (binding.Sort is { } sort && Same(sort.TrimStart('-'), field))
        || (binding.Where?.Keys.Any(k => Same(k, field)) ?? false);

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
