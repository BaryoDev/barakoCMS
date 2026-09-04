using System.Text.Json.Nodes;
using barakoCMS.Features.Public;
using barakoCMS.Models;

namespace barakoCMS.Infrastructure.OpenApi;

/// <summary>
/// Projects the content types into the OpenAPI paths and schemas the delivery API actually serves,
/// so a consumer reads <c>/api/public/students</c> rather than <c>/api/public/{type}</c>.
/// </summary>
/// <remarks>
/// No route is added and no delivery code changes. <c>/api/public/{type}</c> keeps matching exactly
/// as before; only the document expands. A content type is created at runtime, so no build-time
/// mechanism could ever emit this.
///
/// <para><b>A schema is disclosure, so this is an allowlist.</b> It emits exactly what
/// <see cref="PublicDelivery.ToPublic"/> would return to an anonymous caller and nothing else:</para>
/// <list type="bullet">
/// <item>only a type with <see cref="ContentTypeDefinition.IsPubliclyDeliverable"/> appears, not
/// even by name, because a type nobody can fetch is not something the document should confirm
/// exists;</item>
/// <item>only a field whose <see cref="FieldDefinition.Sensitivity"/> is
/// <see cref="SensitivityLevel.Public"/> appears. A field name is itself information: a type
/// carrying <c>guardianContactNumber</c> tells a reader what to go looking for even when every
/// value comes back masked;</item>
/// <item><see cref="FieldDefinition.ValidationRules"/> is never emitted. A regex encodes a business
/// rule or an upstream system's key shape, and this is a read-only API where a caller has nothing
/// to submit;</item>
/// <item><see cref="FieldDefinition.DefaultValue"/> is never emitted. It can carry seeded or
/// internal data.</item>
/// </list>
///
/// <para>The document does not vary by caller: it is the anonymous one, the same for everybody, so
/// it cannot leak an editor's view to a reader through a shared cache. Per-role documents would be
/// more informative and would make every leak above reachable again; that decision is deliberately
/// not taken here.</para>
///
/// <para>The slug route is emitted only for a type <see cref="PublicDelivery.SlugField"/> finds a
/// slug on, because that is the same condition the endpoint 404s on.</para>
/// </remarks>
internal static class DeliveryDocument
{
    /// <summary>
    /// The paths and schemas for <paramref name="types"/>, as
    /// <c>{ "paths": {...}, "schemas": {...} }</c>. Empty objects when nothing is deliverable.
    /// </summary>
    public static JsonObject Build(IEnumerable<ContentTypeDefinition> types)
    {
        var paths = new JsonObject();
        var schemas = new JsonObject();

        var deliverable = types
            .Where(t => t is { IsPubliclyDeliverable: true } && !string.IsNullOrWhiteSpace(t.Name))
            .OrderBy(t => t.Name, StringComparer.Ordinal);

        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var type in deliverable)
        {
            // Two types whose names differ only in a way SchemaName flattens would overwrite each
            // other's schema. First one wins rather than a silently wrong schema on the second.
            var schemaName = SchemaName(type.Name);
            if (!seen.Add(schemaName))
                continue;

            AddType(type, schemaName, paths, schemas);
        }

        return new JsonObject { ["paths"] = paths, ["schemas"] = schemas };
    }

    private static void AddType(ContentTypeDefinition type, string schemaName, JsonObject paths, JsonObject schemas)
    {
        var entry = $"Public{schemaName}";
        var fields = $"{entry}Fields";
        var page = $"{entry}Page";
        var results = $"{entry}SearchResults";
        var title = string.IsNullOrWhiteSpace(type.DisplayName) ? type.Name : type.DisplayName;

        schemas[fields] = FieldsSchema(type);
        schemas[entry] = EntrySchema(fields, title);
        schemas[page] = PageSchema(entry);
        schemas[results] = SearchResultsSchema(entry);

        var route = $"/api/public/{type.Name}";

        paths[route] = new JsonObject
        {
            ["get"] = Operation(
                operationId: $"{entry}List",
                summary: $"List published {title}",
                description: "Published entries only, newest first, with public fields only.",
                schemaRef: page,
                parameters: new JsonArray(
                    QueryParameter("page", "integer", "Page number, 1-indexed."),
                    QueryParameter("pageSize", "integer", $"Items per page, 1 to {PaginatedRequest.MaxPageSize}."),
                    QueryParameter("include", "string", $"Comma-separated reference fields to resolve inline, at most {PublicDelivery.MaxIncludes}."))),
        };

        paths[$"{route}/search"] = new JsonObject
        {
            ["get"] = Operation(
                operationId: $"{entry}Search",
                summary: $"Search published {title}",
                description: "Matches over public fields only. A query shorter than 2 characters returns no results.",
                schemaRef: results,
                parameters: new JsonArray(
                    QueryParameter("q", "string", "The search query. At least 2 characters."),
                    QueryParameter("limit", "integer", "Maximum results, 1 to 50."))),
        };

        // Only when the type is actually slug-addressable: the endpoint 404s otherwise, and a
        // documented path that always 404s is worse than a missing one.
        if (PublicDelivery.SlugField(type) is not null)
        {
            paths[$"{route}/{{slug}}"] = new JsonObject
            {
                ["get"] = Operation(
                    operationId: $"{entry}GetBySlug",
                    summary: $"Get one {title} by slug",
                    description: "404 when the entry is not published, not public, or does not exist.",
                    schemaRef: entry,
                    parameters: new JsonArray(PathParameter("slug", "The entry's slug."))),
            };
        }
    }

    private static JsonObject Operation(
        string operationId, string summary, string description, string schemaRef, JsonArray parameters) =>
        new()
        {
            ["tags"] = new JsonArray("Delivery"),
            ["operationId"] = operationId,
            ["summary"] = summary,
            ["description"] = description,
            ["parameters"] = parameters,
            ["responses"] = new JsonObject
            {
                ["200"] = new JsonObject
                {
                    ["description"] = "Success",
                    ["content"] = new JsonObject
                    {
                        ["application/json"] = new JsonObject { ["schema"] = Ref(schemaRef) },
                    },
                },
                ["404"] = new JsonObject { ["description"] = "Not found" },
            },
        };

    private static JsonObject QueryParameter(string name, string type, string description) =>
        new()
        {
            ["name"] = name,
            ["in"] = "query",
            ["required"] = false,
            ["description"] = description,
            ["schema"] = new JsonObject { ["type"] = type },
        };

    private static JsonObject PathParameter(string name, string description) =>
        new()
        {
            ["name"] = name,
            ["in"] = "path",
            ["required"] = true,
            ["description"] = description,
            ["schema"] = new JsonObject { ["type"] = "string" },
        };

    private static JsonObject FieldsSchema(ContentTypeDefinition type)
    {
        var properties = new JsonObject();
        var required = new JsonArray();

        foreach (var field in type.Fields)
        {
            if (field.Sensitivity != SensitivityLevel.Public)
                continue;
            if (string.IsNullOrWhiteSpace(field.Name))
                continue;
            if (properties.ContainsKey(field.Name))
                continue;

            properties[field.Name] = FieldSchema(field);
            if (field.IsRequired)
                required.Add(field.Name);
        }

        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
        };

        // "required" here means "always present on a delivered entry", which is what IsRequired
        // buys on the write path. An empty array is invalid OpenAPI, so it is omitted.
        if (required.Count > 0)
            schema["required"] = required;

        return schema;
    }

    private static JsonObject FieldSchema(FieldDefinition field)
    {
        var schema = OpenApiType(field.Type);
        if (!string.IsNullOrWhiteSpace(field.DisplayName))
            schema["title"] = field.DisplayName;
        return schema;
    }

    /// <summary>
    /// The field types are the ones <c>FieldTypeRegistry</c> accepts. An unrecognised type gets no
    /// "type" keyword, which in OpenAPI means "anything", rather than a guess that would be wrong.
    /// </summary>
    private static JsonObject OpenApiType(string? fieldType) => (fieldType ?? string.Empty).ToLowerInvariant() switch
    {
        "string" or "text" or "richtext" or "markdown" or "slug" or "time" => new JsonObject { ["type"] = "string" },
        "email" => new JsonObject { ["type"] = "string", ["format"] = "email" },
        "url" => new JsonObject { ["type"] = "string", ["format"] = "uri" },
        "uuid" or "reference" => new JsonObject { ["type"] = "string", ["format"] = "uuid" },
        "date" => new JsonObject { ["type"] = "string", ["format"] = "date" },
        "datetime" => new JsonObject { ["type"] = "string", ["format"] = "date-time" },
        "int" => new JsonObject { ["type"] = "integer", ["format"] = "int64" },
        "decimal" or "money" => new JsonObject { ["type"] = "number" },
        "bool" => new JsonObject { ["type"] = "boolean" },
        "array" => new JsonObject { ["type"] = "array", ["items"] = new JsonObject() },
        "json" or "object" => new JsonObject { ["type"] = "object" },
        "geopoint" => new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["lat"] = new JsonObject { ["type"] = "number", ["minimum"] = -90, ["maximum"] = 90 },
                ["lng"] = new JsonObject { ["type"] = "number", ["minimum"] = -180, ["maximum"] = 180 },
            },
            ["required"] = new JsonArray("lat", "lng"),
        },
        _ => new JsonObject(),
    };

    private static JsonObject EntrySchema(string fieldsRef, string title) => new()
    {
        ["type"] = "object",
        ["title"] = title,
        ["properties"] = new JsonObject
        {
            ["id"] = new JsonObject { ["type"] = "string", ["format"] = "uuid" },
            ["contentType"] = new JsonObject { ["type"] = "string" },
            ["slug"] = new JsonObject { ["type"] = "string", ["nullable"] = true },
            ["data"] = Ref(fieldsRef),
            ["createdAt"] = new JsonObject { ["type"] = "string", ["format"] = "date-time" },
            ["updatedAt"] = new JsonObject { ["type"] = "string", ["format"] = "date-time" },
            ["distanceKm"] = new JsonObject
            {
                ["type"] = "number",
                ["description"] = "Kilometres from the centre of a near filter. Present only when the request had one.",
            },
        },
    };

    private static JsonObject PageSchema(string entryRef) => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["items"] = new JsonObject { ["type"] = "array", ["items"] = Ref(entryRef) },
            ["page"] = new JsonObject { ["type"] = "integer" },
            ["pageSize"] = new JsonObject { ["type"] = "integer" },
            ["totalItems"] = new JsonObject { ["type"] = "integer" },
            ["totalPages"] = new JsonObject { ["type"] = "integer" },
            ["hasNextPage"] = new JsonObject { ["type"] = "boolean" },
            ["hasPreviousPage"] = new JsonObject { ["type"] = "boolean" },
        },
    };

    private static JsonObject SearchResultsSchema(string entryRef) => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["results"] = new JsonObject { ["type"] = "array", ["items"] = Ref(entryRef) },
            ["count"] = new JsonObject { ["type"] = "integer" },
            ["query"] = new JsonObject { ["type"] = "string" },
        },
    };

    private static JsonObject Ref(string schemaName) =>
        new() { ["$ref"] = $"#/components/schemas/{schemaName}" };

    /// <summary>
    /// A content type name is user input, and an OpenAPI schema name has to be a plain identifier
    /// for a generator to make a class out of it.
    /// </summary>
    internal static string SchemaName(string contentTypeName)
    {
        var chars = contentTypeName
            .Select(c => char.IsLetterOrDigit(c) ? c : ' ')
            .ToArray();

        var words = new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var name = string.Concat(words.Select(w => char.ToUpperInvariant(w[0]) + w[1..]));

        // A leading digit is not a valid identifier in most generated languages.
        if (name.Length == 0 || char.IsDigit(name[0]))
            name = "Type" + name;

        return name;
    }
}
