using System.Text.Json;
using System.Text.Json.Serialization;

namespace barakoCMS.Infrastructure.Serialization;

/// <summary>
/// Deserializes the <c>Dictionary&lt;string, object&gt;</c> bags that carry all of barakoCMS's
/// schemaless data — a content type's <c>Data</c>, a permission rule's <c>Conditions</c>, an audit
/// entry's <c>Metadata</c>, workflow action parameters — into plain CLR types, consistently at every
/// depth, preferring <see cref="decimal"/> over <see cref="double"/> for fractional numbers.
///
/// Two problems this fixes, both verified against a real Marten round-trip:
///
/// 1. <b>Money silently became <c>double</c>.</b> Storing <c>1234.56m</c> read back as
///    <see cref="double"/>. Survivable for a single value, but summing thousands of them — exactly
///    what a ledger balance does — accumulates binary floating-point drift. An accounting total that
///    is plausible but wrong is the worst failure mode in this codebase.
///
/// 2. <b>Depth-inconsistent types.</b> Only the top level was unwrapped into CLR primitives; anything
///    nested stayed a raw <see cref="JsonElement"/>. Not theoretical: that inconsistency silently
///    broke every persisted conditional permission rule, because <c>ConditionEvaluator</c> checked
///    for <c>Dictionary&lt;string, object&gt;</c> and quietly got a <see cref="JsonElement"/>.
///
/// <b>Why this targets the dictionary and not <c>object</c>:</b> Marten's
/// <c>SystemTextJsonSerializer</c> registers its own <c>SystemObjectNewtonsoftCompatibleConverter</c>
/// for <c>object</c> at index 0 of its deserialize options — after any <c>configure</c> callback runs
/// — and System.Text.Json picks the first matching converter, so an <c>object</c>-targeted converter
/// registered here can never win. Targeting <c>Dictionary&lt;string, object&gt;</c> sidesteps that
/// entirely: Marten's converter doesn't claim that type, so this one is consulted and then controls
/// how every value beneath it is read.
///
/// Number rule, in order: whole numbers that fit become <see cref="long"/> (preserving previous
/// behaviour for ids and counts), fractional numbers become <see cref="decimal"/>, and anything
/// outside decimal's range falls back to <see cref="double"/> rather than throwing.
/// </summary>
public sealed class ObjectJsonConverter : JsonConverter<Dictionary<string, object>>
{
    public override Dictionary<string, object> Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException($"Expected an object, found {reader.TokenType}.");

        return (Dictionary<string, object>)ReadObject(ref reader)!;
    }

    private static object? ReadValue(ref Utf8JsonReader reader)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.True:
                return true;
            case JsonTokenType.False:
                return false;
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.String:
                return reader.GetString();

            case JsonTokenType.Number:
                // Whole numbers stay integral so ids and counts don't become 42.0m.
                if (reader.TryGetInt64(out var l)) return l;
                // The point of this converter: keep money exact.
                if (reader.TryGetDecimal(out var dec)) return dec;
                // Outside decimal's range (very large/small scientific values) — don't throw.
                return reader.GetDouble();

            case JsonTokenType.StartObject:
                return ReadObject(ref reader);

            case JsonTokenType.StartArray:
                return ReadArray(ref reader);

            default:
                throw new JsonException($"Unexpected token {reader.TokenType} while reading a value.");
        }
    }

    private static Dictionary<string, object> ReadObject(ref Utf8JsonReader reader)
    {
        var dict = new Dictionary<string, object>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                return dict;

            var name = reader.GetString()!;
            reader.Read();
            dict[name] = ReadValue(ref reader)!;
        }
        throw new JsonException("Unexpected end of JSON while reading an object.");
    }

    private static List<object> ReadArray(ref Utf8JsonReader reader)
    {
        var list = new List<object>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
                return list;

            list.Add(ReadValue(ref reader)!);
        }
        throw new JsonException("Unexpected end of JSON while reading an array.");
    }

    public override void Write(
        Utf8JsonWriter writer, Dictionary<string, object> value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var (key, item) in value)
        {
            writer.WritePropertyName(key);
            WriteValue(writer, item, options);
        }
        writer.WriteEndObject();
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        // Dispatch on the runtime type so a decimal is written as a decimal rather than being
        // widened, and so nested bags recurse through this same logic.
        switch (value)
        {
            case decimal d: writer.WriteNumberValue(d); return;
            case long v: writer.WriteNumberValue(v); return;
            case int v: writer.WriteNumberValue(v); return;
            case double v: writer.WriteNumberValue(v); return;
            case float v: writer.WriteNumberValue(v); return;
            case bool v: writer.WriteBooleanValue(v); return;
            case string v: writer.WriteStringValue(v); return;
            case Dictionary<string, object> nested:
                writer.WriteStartObject();
                foreach (var (k, item) in nested)
                {
                    writer.WritePropertyName(k);
                    WriteValue(writer, item, options);
                }
                writer.WriteEndObject();
                return;
            case System.Collections.IEnumerable seq and not string:
                writer.WriteStartArray();
                foreach (var item in seq) WriteValue(writer, item, options);
                writer.WriteEndArray();
                return;
            default:
                JsonSerializer.Serialize(writer, value, value.GetType(), options);
                return;
        }
    }
}
