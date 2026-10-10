using System.Text.Json;
using System.Text.Json.Nodes;

namespace barakoCMS.Core.Validation;

/// <summary>Compares what a write sends for a field with what the entry already holds there.</summary>
public static class StoredValues
{
    /// <summary>
    /// Whether the write leaves this field holding what the entry already holds. False when there is
    /// no entry, or nothing is stored under the field.
    /// </summary>
    public static bool IsUnchanged(string fieldName, object? value, Models.Content? existing)
    {
        if (existing?.Data is not { } stored)
            return false;

        foreach (var (key, held) in stored)
        {
            if (string.Equals(key, fieldName, StringComparison.OrdinalIgnoreCase) && held is not null)
                return JsonNode.DeepEquals(JsonSerializer.SerializeToNode(held), JsonSerializer.SerializeToNode(value));
        }

        return false;
    }
}
