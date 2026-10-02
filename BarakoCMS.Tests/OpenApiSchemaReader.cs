using System.Text.Json;
using FluentAssertions;

namespace BarakoCMS.Tests;

/// <summary>Reads schemas out of the served OpenAPI document, following references to where the content is.</summary>
/// <remarks>
/// A property that is an enum or an object is written as a reference, sometimes wrapped in a
/// one-entry <c>allOf</c> or <c>oneOf</c>, so a test that read the property's own node would find
/// neither the values nor the properties it is asking about.
/// </remarks>
internal static class OpenApiSchemaReader
{
    private const string SchemaPrefix = "#/components/schemas/";

    public static JsonElement Operation(JsonDocument doc, string path, string method) =>
        doc.RootElement.GetProperty("paths").GetProperty(path).GetProperty(method);

    public static JsonElement RequestBody(JsonDocument doc, JsonElement operation) =>
        Resolve(doc, FirstContentSchema(operation.GetProperty("requestBody")));

    public static JsonElement OkResponse(JsonDocument doc, JsonElement operation) =>
        Resolve(doc, FirstContentSchema(operation.GetProperty("responses").GetProperty("200")));

    public static JsonElement Property(JsonDocument doc, JsonElement schema, string name) =>
        Resolve(doc, schema.GetProperty("properties").GetProperty(name));

    public static List<string> PropertyNames(JsonElement schema) =>
        schema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToList();

    public static List<string> EnumValues(JsonElement schema) =>
        schema.GetProperty("enum").EnumerateArray().Select(v => v.ToString()).ToList();

    public static JsonElement Resolve(JsonDocument doc, JsonElement schema)
    {
        for (var hops = 0; hops < 10; hops++)
        {
            if (schema.TryGetProperty("enum", out _) || schema.TryGetProperty("properties", out _))
            {
                return schema;
            }

            if (schema.TryGetProperty("$ref", out var reference))
            {
                schema = Named(doc, reference.GetString()!);
            }
            else if (Only(schema, "allOf") is { } all)
            {
                schema = all;
            }
            else if (Only(schema, "oneOf") is { } one)
            {
                schema = one;
            }
            else
            {
                return schema;
            }
        }

        throw new InvalidOperationException("The schema refers to itself.");
    }

    private static JsonElement FirstContentSchema(JsonElement bodyOrResponse)
    {
        var content = bodyOrResponse.GetProperty("content").EnumerateObject().ToList();
        content.Should().NotBeEmpty("a body with no content type has no schema to read");
        return content[0].Value.GetProperty("schema");
    }

    private static JsonElement Named(JsonDocument doc, string reference)
    {
        reference.Should().StartWith(SchemaPrefix);
        return doc.RootElement.GetProperty("components").GetProperty("schemas")
            .GetProperty(reference[SchemaPrefix.Length..]);
    }

    private static JsonElement? Only(JsonElement schema, string keyword) =>
        schema.TryGetProperty(keyword, out var list)
        && list.ValueKind == JsonValueKind.Array
        && list.GetArrayLength() == 1
            ? list[0]
            : null;
}
