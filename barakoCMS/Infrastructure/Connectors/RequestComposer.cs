using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using barakoCMS.Infrastructure.Security;
using barakoCMS.Models;
using Marten;

namespace barakoCMS.Infrastructure.Connectors;

/// <summary>A composed request, or the reason it will not be sent.</summary>
/// <remarks>
/// The credential is deliberately absent. This is what the dry-run endpoint returns and what the
/// action logs, so anything in here is something an operator can read.
/// </remarks>
public sealed record ComposedRequest(
    string Method,
    string Url,
    Dictionary<string, string> Headers,
    string? Body,
    string? BodyContentType)
{
    public static ComposedRequest Refused(string reason) =>
        new(string.Empty, string.Empty, new(), null, null) { Refusal = reason };

    /// <summary>Why this will not be sent, or null.</summary>
    public string? Refusal { get; private init; }

    public bool Ok => Refusal is null;
}

public interface IRequestComposer
{
    /// <summary>
    /// Builds the request a definition describes for one content item, without sending it.
    /// </summary>
    Task<ComposedRequest> ComposeAsync(RequestDefinition definition, Connector connector, Content content, CancellationToken ct);
}

internal sealed class RequestComposer : IRequestComposer
{
    /// <summary>The same <c>{{name}}</c> syntax workflow actions already use.</summary>
    private static readonly Regex Hole = new(@"\{\{\s*([A-Za-z0-9_.\[\]]+)\s*\}\}", RegexOptions.Compiled);

    private readonly IQuerySession _session;
    private readonly IConfiguration _config;

    public RequestComposer(IQuerySession session, IConfiguration config)
    {
        _session = session;
        _config = config;
    }

    public async Task<ComposedRequest> ComposeAsync(
        RequestDefinition definition, Connector connector, Content content, CancellationToken ct)
    {
        var schema = await _session.Query<ContentTypeDefinition>()
            .FirstOrDefaultAsync(d => d.Name == content.ContentType, ct);

        // Refused rather than composed against no schema. Without the field definitions there is no
        // way to tell a Public field from a Sensitive one, and the whole sensitivity check below
        // would pass by having nothing to check: the shape of defect this project keeps finding.
        if (schema is null)
        {
            return ComposedRequest.Refused(
                $"Content type '{content.ContentType}' has no definition, so field sensitivity cannot be checked.");
        }

        var templates = new List<string> { definition.PathTemplate };
        templates.AddRange(definition.HeaderTemplates.Values);
        if (definition.BodyTemplate is not null) templates.Add(definition.BodyTemplate);

        var refusal = Refuse(templates, schema);
        if (refusal is not null) return ComposedRequest.Refused(refusal);

        // Path and headers are not JSON, whatever the body is. Escaping a path segment as if it were
        // a JSON string would put backslashes in a URL.
        var path = Substitute(definition.PathTemplate, content, Escaping.Url);

        var headers = new Dictionary<string, string>(definition.HeaderTemplates.Count);
        foreach (var (name, template) in definition.HeaderTemplates)
        {
            headers[name] = Substitute(template, content, Escaping.None);
        }

        var isJson = definition.BodyContentType.Contains("json", StringComparison.OrdinalIgnoreCase);
        var body = definition.BodyTemplate is null
            ? null
            : Substitute(definition.BodyTemplate, content, isJson ? Escaping.Json : Escaping.None);

        if (body is not null && isJson)
        {
            // Parsed before it is sent, not after it fails. A value that escaped correctly still
            // produces invalid JSON if the template itself is malformed, and a provider's answer to
            // that is a 400 whose message describes their parser rather than this template.
            try
            {
                using var _ = JsonDocument.Parse(body);
            }
            catch (JsonException ex)
            {
                return ComposedRequest.Refused($"The composed body is not valid JSON: {ex.Message}");
            }
        }

        if (!Uri.TryCreate(connector.BaseUrl, UriKind.Absolute, out var baseUri))
        {
            return ComposedRequest.Refused($"Connector '{connector.Slug}' has no absolute base URL.");
        }

        if (!Uri.TryCreate(baseUri, path, out var url))
        {
            return ComposedRequest.Refused($"'{path}' does not combine with the connector's base URL.");
        }

        // AbsoluteUri, not ToString. ToString returns the unescaped form for display, so a value
        // escaped into the path comes back out decoded, and this string is parsed back into a Uri
        // before the send: a space would break the parse and a slash would address a different
        // endpoint than the one the escaping was there to prevent.
        return new ComposedRequest(definition.Method.ToUpperInvariant(), url.AbsoluteUri, headers, body, definition.BodyContentType);
    }

    /// <summary>Returns why these templates may not be sent, or null.</summary>
    /// <remarks>
    /// Refusing beats redacting. The operator wrote <c>{{SSN}}</c> on purpose, and a request that
    /// silently posts <c>***</c> where they expected a value looks like it worked. They should be
    /// told the template cannot run, at the moment they can still change it.
    /// </remarks>
    private static string? Refuse(IEnumerable<string> templates, ContentTypeDefinition schema)
    {
        foreach (var template in templates)
        {
            foreach (Match match in Hole.Matches(template ?? string.Empty))
            {
                var name = match.Groups[1].Value;

                if (name.StartsWith("query.", StringComparison.OrdinalIgnoreCase))
                {
                    // Not silently left as a literal. Posting the text "{{query.rows}}" to a third
                    // party is worse than not running: it looks like a delivery and is a defect.
                    return $"'{match.Value}' needs a query, and queries are not implemented yet (#328). "
                         + "Remove it or wait for that.";
                }

                var field = FieldFor(name, schema);
                if (field is not null && field.Sensitivity != SensitivityLevel.Public)
                {
                    return $"'{match.Value}' is {field.Sensitivity} on '{schema.Name}', and this request "
                         + "would send it to a third party. A field that is not Public cannot leave, "
                         + "even when a template names it.";
                }
            }
        }

        return null;
    }

    /// <summary>The schema field a variable names, or null when it names a system variable.</summary>
    private static FieldDefinition? FieldFor(string name, ContentTypeDefinition schema)
    {
        // "data.Title" and "Title" both name the same field: the admin's variable picker offers the
        // first and templates in the wild use the second.
        var bare = name.StartsWith("data.", StringComparison.OrdinalIgnoreCase) ? name[5..] : name;

        return schema.Fields.FirstOrDefault(f => string.Equals(f.Name, bare, StringComparison.OrdinalIgnoreCase));
    }

    private enum Escaping { None, Json, Url }

    /// <summary>
    /// Substitutes each hole with its value, escaped for where it lands.
    /// </summary>
    /// <remarks>
    /// Not delegated to <c>ITemplateVariableExtractor.ResolveVariables</c>, and the reason is the
    /// escaping. That method returns a finished string, so there is no point at which a single value
    /// can be escaped for its context: a title containing a double quote would produce a malformed
    /// body, and one containing <c>","admin":true</c> would produce a request the operator did not
    /// write. That is injection wearing a different costume. The variable names here are the same
    /// ones it resolves, deliberately, so an operator moves a template between the two unchanged.
    /// </remarks>
    private string Substitute(string template, Content content, Escaping escaping)
    {
        return Hole.Replace(template, match =>
        {
            var raw = Value(match.Groups[1].Value, content);
            if (raw is null) return match.Value;

            return escaping switch
            {
                // Serialised as a JSON string and then unwrapped, so the escaping is the
                // serialiser's rather than a hand-rolled replace that forgets a control character.
                Escaping.Json => JsonEncodedText.Encode(raw).ToString(),
                Escaping.Url => Uri.EscapeDataString(raw),
                _ => raw,
            };
        });
    }

    private string? Value(string name, Content content)
    {
        switch (name.ToLowerInvariant())
        {
            case "id": return content.Id.ToString();
            case "contenttype": return content.ContentType;
            case "status": return content.Status.ToString();
            case "createdat": return content.CreatedAt.ToString("O");
            case "updatedat": return content.UpdatedAt.ToString("O");

            // The single most likely thing anyone templates, and it did not exist. Built from the
            // configured public URL rather than a request host: this composes inside a workflow,
            // where there is no request, and a host header is not somewhere to learn your own name.
            case "publicurl":
            {
                var baseUrl = _config[CanonicalHost.BaseUrlKey]?.Trim().TrimEnd('/');
                if (string.IsNullOrEmpty(baseUrl)) return null;

                var slug = content.Data.TryGetValue("Slug", out var s) ? s?.ToString() : null;
                return string.IsNullOrWhiteSpace(slug)
                    ? $"{baseUrl}/api/public/{content.ContentType}/{content.Id}"
                    : $"{baseUrl}/api/public/{content.ContentType}/{slug}";
            }
        }

        var bare = name.StartsWith("data.", StringComparison.OrdinalIgnoreCase) ? name[5..] : name;

        foreach (var (key, value) in content.Data)
        {
            if (string.Equals(key, bare, StringComparison.OrdinalIgnoreCase))
            {
                return value?.ToString();
            }
        }

        // Left as the literal hole rather than emptied. An unresolved variable is almost always a
        // typo, and a body with {{Titel}} still in it is a great deal easier to diagnose than one
        // with a silently missing field.
        return null;
    }
}

/// <summary>Whether a response counts as the call having worked.</summary>
internal static class SuccessEvaluator
{
    internal static bool Succeeded(SuccessRule rule, int statusCode, string? body, string? jsonPath)
    {
        switch (rule)
        {
            case SuccessRule.AnyResponse:
                return true;

            case SuccessRule.TwoHundredAndJsonPathAbsent:
                if (statusCode is < 200 or > 299) return false;
                if (string.IsNullOrWhiteSpace(jsonPath)) return true;
                return !JsonPathPresent(body, jsonPath);

            default:
                return statusCode is >= 200 and <= 299;
        }
    }

    /// <summary>
    /// Whether a dotted path resolves to anything in the body.
    /// </summary>
    /// <remarks>
    /// A body that will not parse counts as the path being absent. The alternative is calling every
    /// unparseable response a failure, which would fail every provider that answers 200 with an
    /// empty body, and this rule exists for providers whose bodies are the problem.
    /// </remarks>
    private static bool JsonPathPresent(string? body, string path)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;

        try
        {
            using var doc = JsonDocument.Parse(body);
            var node = doc.RootElement;

            foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(segment, out node))
                {
                    return false;
                }
            }

            return node.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
