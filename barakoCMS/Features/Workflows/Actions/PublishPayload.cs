using System.Text.Json;
using System.Text.RegularExpressions;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;

namespace barakoCMS.Features.Workflows.Actions;

/// <summary>
/// What a Publish action may send: a JSON object of text values, whose placeholders come from a short
/// allowlist, and none of which names a field that is not Public.
/// </summary>
/// <remarks>
/// Nothing is sent unless the payload names it. The allowlist is the entry's id, type, status and two
/// timestamps, and <c>data.Field</c>. Anything else a template can reach (the author, the transition's
/// actor, links, a followed reference, a loop) is a person's data or a second entry's, and is refused
/// when the workflow is saved.
/// </remarks>
internal static class PublishPayload
{
    public const string PayloadParameter = "Payload";

    private static readonly HashSet<string> EntryNames = new(StringComparer.Ordinal)
    {
        "id", "contentType", "status", "createdAt", "updatedAt",
    };

    private static readonly Regex DataName = new(@"\Adata\.(?<field>[A-Za-z0-9_]+)\z", RegexOptions.Compiled);

    /// <summary>The properties of the payload, or the reason it is not one.</summary>
    public static (Dictionary<string, string>? Properties, string? Error) Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return (null, "Payload is required");
        if (json.Length > TemplateExpression.MaxTemplateLength) return (null, "Payload is too long");

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return (null, "Payload must be a JSON object");

            var properties = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                    return (null, $"Payload property '{property.Name}' must be text. Name a field with a placeholder such as {{{{data.Title}}}}");

                properties[property.Name] = property.Value.GetString() ?? string.Empty;
            }

            return properties.Count == 0
                ? (null, "Payload must name at least one property, because nothing is sent unless it is named")
                : (properties, null);
        }
        catch (JsonException)
        {
            return (null, "Payload must be valid JSON");
        }
    }

    /// <summary>The placeholders in a value that the allowlist does not cover, and the data fields it does name.</summary>
    public static (List<string> Refused, List<string> Fields) Placeholders(IEnumerable<string> values)
    {
        var refused = new List<string>();
        var fields = new List<string>();

        foreach (var value in values)
        {
            foreach (Match hole in TemplateExpression.Token.Matches(value))
            {
                var body = hole.Groups[1].Value;
                var name = body.Split('|')[0].Trim();

                if (EntryNames.Contains(name)) continue;

                if (DataName.Match(name) is { Success: true } data)
                {
                    fields.Add(data.Groups["field"].Value);
                    continue;
                }

                refused.Add("{{" + body.Trim() + "}}");
            }
        }

        return (refused, fields);
    }

    /// <summary>The fields among <paramref name="fields"/> that the type does not mark Public.</summary>
    public static List<string> NonPublic(IEnumerable<string> fields, ContentTypeDefinition definition) =>
        fields
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(name => definition.Fields.FirstOrDefault(f =>
                f is not null && string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)) is { } field
                && field.Sensitivity != SensitivityLevel.Public)
            .ToList();

    /// <summary>Every problem with a payload, against the types the workflow fires for. Empty when it can be saved.</summary>
    public static List<string> Problems(string? json, IEnumerable<ContentTypeDefinition> definitions)
    {
        var (properties, error) = Parse(json);
        if (properties is null) return [error!];

        var problems = new List<string>();
        var (refused, fields) = Placeholders(properties.Values);

        foreach (var placeholder in refused.Distinct())
        {
            problems.Add($"{placeholder} cannot be published. Use id, contentType, status, createdAt, updatedAt or data.<Field>");
        }

        foreach (var definition in definitions)
        {
            foreach (var field in NonPublic(fields, definition))
            {
                problems.Add($"Field '{field}' on '{definition.Name}' is not Public, so it cannot be published");
            }
        }

        return problems;
    }
}
