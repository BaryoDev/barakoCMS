using System.Text.Json;

namespace barakoCMS.Infrastructure.Services;

internal static class JsonValues
{
    /// <summary>
    /// Recursively converts a <see cref="JsonElement"/> into the equivalent plain CLR value
    /// (Dictionary, List, string, long or double, bool, or null), leaving a value that is already
    /// plain untouched. A Role read back from Marten holds its condition values as JsonElement, so
    /// the evaluator and the predicate compiler both apply this where a condition value is read.
    /// </summary>
    internal static object? ToClr(object? value)
    {
        if (value is not JsonElement element)
            return value;

        return element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject()
                .ToDictionary(p => p.Name, p => ToClr(p.Value)!),
            JsonValueKind.Array => element.EnumerateArray().Select(e => ToClr(e)).ToList(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => value,
        };
    }
}
