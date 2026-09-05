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
    private readonly IQueryRunner _queryRunner;

    public RequestComposer(IQuerySession session, IConfiguration config, IQueryRunner queryRunner)
    {
        _session = session;
        _config = config;
        _queryRunner = queryRunner;
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

        // Resolved once, before either refusal check, so both a Sensitive field and a bad query hole
        // are checked against the same set of templates and neither is composed while the other
        // could still be the reason nothing should be sent.
        var query = await ResolveQueryAsync(definition, templates, ct);
        if (query.Refusal is not null) return ComposedRequest.Refused(query.Refusal);

        var refusal = Refuse(templates, schema);
        if (refusal is not null) return ComposedRequest.Refused(refusal);

        // Path and headers are not JSON, whatever the body is. Escaping a path segment as if it were
        // a JSON string would put backslashes in a URL.
        var path = Substitute(definition.PathTemplate, content, query.Rows, Escaping.Url);

        var headers = new Dictionary<string, string>(definition.HeaderTemplates.Count);
        foreach (var (name, template) in definition.HeaderTemplates)
        {
            headers[name] = Substitute(template, content, query.Rows, Escaping.None);
        }

        var isJson = definition.BodyContentType.Contains("json", StringComparison.OrdinalIgnoreCase);
        var body = definition.BodyTemplate is null
            ? null
            : Substitute(definition.BodyTemplate, content, query.Rows, isJson ? Escaping.Json : Escaping.None);

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
    ///
    /// A <c>{{query.*}}</c> hole is checked separately, by <see cref="ResolveQueryAsync"/>, before
    /// this runs: it needs a database round trip that this method, deliberately synchronous, cannot
    /// make. Nothing here matches a schema field named "query.something", so those holes pass
    /// through this check with nothing to say about them, which is correct: they already had their
    /// chance to be refused.
    /// </remarks>
    private static string? Refuse(IEnumerable<string> templates, ContentTypeDefinition schema)
    {
        foreach (var template in templates)
        {
            foreach (Match match in Hole.Matches(template ?? string.Empty))
            {
                var name = match.Groups[1].Value;
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

    /// <summary>The rows a <c>{{query.*}}</c> hole may draw from, or the reason none may run.</summary>
    private readonly record struct QueryContext(IReadOnlyList<Dictionary<string, object>> Rows, string? Refusal)
    {
        public static readonly QueryContext None = new([], null);
    }

    /// <summary>
    /// Resolves <see cref="RequestDefinition.QuerySlug"/> and runs it, if any template needs it.
    /// </summary>
    /// <remarks>
    /// Run at most once per compose, however many <c>{{query.*}}</c> holes reference it, and not at
    /// all when none do: a request with a <c>QuerySlug</c> but no hole naming it should not pay for
    /// a query nothing in the message uses.
    ///
    /// <see cref="IQueryRunner.RunAsync"/> re-validates the definition against the current schema
    /// before running it, the same as the preview and manual-run endpoints do, so a field raised to
    /// Sensitive after the query was saved is caught here too, not only in the admin screen that
    /// saved it.
    /// </remarks>
    private async Task<QueryContext> ResolveQueryAsync(
        RequestDefinition definition, List<string> templates, CancellationToken ct)
    {
        var names = new List<string>();
        foreach (var template in templates)
        {
            foreach (Match match in Hole.Matches(template ?? string.Empty))
            {
                var name = match.Groups[1].Value;
                if (name.StartsWith("query.", StringComparison.OrdinalIgnoreCase))
                {
                    names.Add(name);
                }
            }
        }

        if (names.Count == 0) return QueryContext.None;

        // The first hole found names the refusal, which is enough to act on and reads better than a
        // refusal that lists every hole that happens to share the same underlying problem.
        var hole = "{{" + names[0] + "}}";

        if (string.IsNullOrWhiteSpace(definition.QuerySlug))
        {
            return new QueryContext([], $"'{hole}' needs a query, and this request does not name one. "
                + "Set QuerySlug or remove the hole.");
        }

        var queryDefinition = await _session.Query<QueryDefinition>()
            .FirstOrDefaultAsync(q => q.Slug == definition.QuerySlug, ct);

        if (queryDefinition is null)
        {
            return new QueryContext([], $"'{hole}' needs a query, and '{definition.QuerySlug}' does not exist.");
        }

        var result = await _queryRunner.RunAsync(queryDefinition, ct);
        if (!result.Ok)
        {
            return new QueryContext([], $"'{hole}' cannot run: {result.Refusal}");
        }

        // Every field named must be either "rows" or on the query's own allowlist. QueryRunner
        // already restricts what a row can hold to that allowlist; this is what stops a template
        // asking for a field the query never selected in the first place, which QueryRunner has no
        // way to refuse because it never sees the templates.
        foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var field = name["query.".Length..];
            if (string.Equals(field, "rows", StringComparison.OrdinalIgnoreCase)) continue;

            if (!queryDefinition.Fields.Any(f => string.Equals(f, field, StringComparison.OrdinalIgnoreCase)))
            {
                var available = string.Join(", ", new[] { "rows" }.Concat(queryDefinition.Fields));
                var thisHole = "{{" + name + "}}";
                return new QueryContext([], $"'{thisHole}' needs a query, and "
                    + $"'{definition.QuerySlug}' does not select '{field}'. It returns: {available}.");
            }
        }

        return new QueryContext(result.Rows, null);
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
    private string Substitute(
        string template, Content content, IReadOnlyList<Dictionary<string, object>> queryRows, Escaping escaping)
    {
        return Hole.Replace(template, match =>
        {
            var name = match.Groups[1].Value;

            if (name.StartsWith("query.", StringComparison.OrdinalIgnoreCase))
            {
                return SubstituteQuery(name["query.".Length..], queryRows, escaping);
            }

            var raw = Value(name, content);
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

    /// <summary>
    /// The value for a <c>{{query.*}}</c> hole. <see cref="ResolveQueryAsync"/> has already refused
    /// anything that is not "rows" or one of the query's own fields, so this only has to produce it.
    /// </summary>
    /// <remarks>
    /// <c>rows</c> is the one case not re-escaped as a JSON string in a JSON body: it is already a
    /// JSON array, produced by the same projection the preview endpoint returns, and JSON-encoding
    /// that array as a string would hand the recipient a string full of JSON rather than an array. A
    /// scalar field is treated exactly like a content field: read from the first row, escaped for
    /// where it lands, and an empty string rather than the literal hole when there is no first row,
    /// because "the query matched nothing" is an answer, not a typo.
    /// </remarks>
    private static string SubstituteQuery(
        string field, IReadOnlyList<Dictionary<string, object>> rows, Escaping escaping)
    {
        if (string.Equals(field, "rows", StringComparison.OrdinalIgnoreCase))
        {
            var json = JsonSerializer.Serialize(rows);
            return escaping == Escaping.Url ? Uri.EscapeDataString(json) : json;
        }

        var value = rows.Count == 0 ? null : FieldValue(rows[0], field);
        if (value is null) return string.Empty;

        return escaping switch
        {
            Escaping.Json => JsonEncodedText.Encode(value).ToString(),
            Escaping.Url => Uri.EscapeDataString(value),
            _ => value,
        };
    }

    private static string? FieldValue(Dictionary<string, object> row, string field) =>
        row.TryGetValue(field, out var value) ? value?.ToString() : null;

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
