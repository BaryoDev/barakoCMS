using System.Text.Json;
using barakoCMS.Core.Validation;
using barakoCMS.Infrastructure.Jobs;
using barakoCMS.Models;
using Marten;

namespace barakoCMS.Infrastructure.Rollups;

/// <summary>
/// The backfill of rollup values when a rollup is added or changed on a type with entries. It runs
/// on the FieldChanges recompute path, so progress, abandon and resume are the ones a formula has.
/// </summary>
internal static class RollupRecompute
{
    /// <summary>The rollup fields of <paramref name="after"/> that are new or count differently from the stored one.</summary>
    internal static List<string> Changed(IEnumerable<FieldDefinition>? before, IEnumerable<FieldDefinition> after)
    {
        string Key(FieldDefinition f) => JsonSerializer.Serialize(new { f.Rollup, f.Sensitivity });

        var held = (before ?? []).Where(f => f is not null).ToList();
        return after
            .Where(f => f?.Rollup is not null)
            .Where(f => held.FirstOrDefault(h => string.Equals(h.Name, f.Name, StringComparison.OrdinalIgnoreCase)) is not { Rollup: not null } was
                || Key(was) != Key(f))
            .Select(f => f.Name)
            .ToList();
    }

    /// <summary>The value of every rollup field of <paramref name="header"/> for each header id, as the drain computes it.</summary>
    internal static async Task<Dictionary<Guid, Dictionary<string, object?>>> ValuesAsync(
        IDocumentSession session, ContentTypeDefinition header, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        var values = ids.ToDictionary(id => id, _ => new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase));
        var rollups = header.Fields.Where(f => f?.Rollup is not null).ToList();
        if (rollups.Count == 0)
        {
            return values;
        }

        var definitions = await session.Query<ContentTypeDefinition>().ToListAsync(ct);
        foreach (var rollup in rollups)
        {
            var source = RollupDrain.SourceOf(definitions, rollup);
            if (!source.Ok)
            {
                continue;
            }

            foreach (var (id, value) in await RollupAggregate.ValuesAsync(session, rollup, source.Field, ids, ct))
            {
                values[id][rollup.Name] = value;
            }
        }

        return values;
    }
}
