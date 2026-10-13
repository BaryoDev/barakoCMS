using System.Text.Json;
using System.Text.Json.Serialization;

namespace barakoCMS.Features.Config;

/// <summary>The one way a core section writes and reads its documents.</summary>
/// <remarks>
/// Every core provider compares what is stored with what a document declares by writing both
/// through these options and comparing the JSON. Export writes through the same options, so a
/// document exported here and applied back is equal to what is stored by construction, and a second
/// plan is empty.
/// </remarks>
internal static class ConfigJson
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(new barakoCMS.Infrastructure.Serialization.ObjectJsonConverter());
        return options;
    }

    public static JsonElement ToElement<T>(T value) => JsonSerializer.SerializeToElement(value, Options);

    public static bool Same<T>(T stored, T desired) => JsonElement.DeepEquals(ToElement(stored), ToElement(desired));

    /// <summary>Reads one document, or says why it cannot be read.</summary>
    public static T? Read<T>(string key, JsonElement document, List<string> errors) where T : class
    {
        if (document.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"'{key}' is not a JSON object.");
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(document, Options)
                   ?? throw new JsonException("The document is empty.");
        }
        catch (JsonException ex)
        {
            errors.Add($"'{key}' cannot be read: {ex.Message}");
            return null;
        }
    }

    /// <summary>The documents keyed and sorted ordinally, the order an export writes them in.</summary>
    public static IReadOnlyDictionary<string, JsonElement> Sorted(IEnumerable<KeyValuePair<string, JsonElement>> documents)
    {
        var sorted = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (key, value) in documents)
        {
            sorted[key] = value;
        }

        return sorted;
    }
}
