using System.Text.Json;
using System.Text.Json.Nodes;

namespace barakoCMS.Core.Validation;

/// <summary>One field a write would change: its value, or for a definition its type, before and after.</summary>
/// <param name="Field">The field's name as the write spells it.</param>
/// <param name="From">What it holds now, or null when it is new.</param>
/// <param name="To">What it would hold, or null when the write leaves it out.</param>
internal sealed record FieldDiff(string Field, object? From, object? To);

/// <summary>A response that can answer for a dry run, which stored nothing.</summary>
/// <remarks>
/// The idempotency finalizer releases the key of a dry run instead of completing it: the real write
/// that follows, sent with the same key, is a different request and must not be refused as one.
/// </remarks>
internal interface IDryRunResponse
{
    bool DryRun { get; }
}

/// <summary>
/// What a dry run answers: the write was checked by everything the real write runs, and this is
/// what it would change. Nothing is stored.
/// </summary>
internal static class DryRuns
{
    /// <summary>The fields whose value differs between two data bags, keys matched ignoring case.</summary>
    public static List<FieldDiff> Between(IReadOnlyDictionary<string, object>? before, IReadOnlyDictionary<string, object> after)
    {
        before ??= new Dictionary<string, object>();
        var diffs = new List<FieldDiff>();

        foreach (var (key, value) in after)
        {
            var held = before.FirstOrDefault(kv => string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase));
            if (held.Key is null || !Same(held.Value, value))
            {
                diffs.Add(new FieldDiff(key, held.Value, value));
            }
        }

        diffs.AddRange(before
            .Where(kv => !after.Keys.Any(k => string.Equals(k, kv.Key, StringComparison.OrdinalIgnoreCase)))
            .Select(kv => new FieldDiff(kv.Key, kv.Value, null)));

        return diffs;
    }

    private static bool Same(object? a, object? b) =>
        JsonNode.DeepEquals(JsonSerializer.SerializeToNode(a), JsonSerializer.SerializeToNode(b));
}
