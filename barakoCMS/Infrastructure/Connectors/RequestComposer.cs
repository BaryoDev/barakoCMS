using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using barakoCMS.Infrastructure.Security;
using barakoCMS.Infrastructure.Services;
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

    /// <summary>
    /// The headers whose value holds a field the connector was granted on top of the Public ones. A
    /// delivery row stores these redacted, so the grant is not copied into a table anyone with a
    /// delivery view may read.
    /// </summary>
    public IReadOnlyCollection<string> RedactedHeaders { get; init; } = [];
}

public interface IRequestComposer
{
    /// <summary>
    /// Builds the request a definition describes for one content item, without sending it.
    /// </summary>
    /// <param name="idempotencyKey">
    /// The workflow run's key, verbatim, or null when this call has no run behind it (a dry run, a
    /// test, an action invoked some other way). Composing proceeds either way; a null key just
    /// means no idempotency header goes on this particular call.
    /// </param>
    Task<ComposedRequest> ComposeAsync(
        RequestDefinition definition, Connector connector, Content content, string? idempotencyKey, CancellationToken ct);
}

/// <summary>Whether a caller may read a field of an entry. The dry run asks it per granted field.</summary>
internal delegate ValueTask<bool> FieldReadCheck(string contentType, FieldDefinition field, Content entry);

/// <summary>
/// The composer's preview: the same request, with any granted value the caller may not read masked
/// where it lands.
/// </summary>
internal interface IPreviewRequestComposer
{
    Task<ComposedRequest> PreviewAsync(
        RequestDefinition definition, Connector connector, Content content, FieldReadCheck mayRead, CancellationToken ct);
}

internal sealed class RequestComposer(
    IQuerySession session,
    IConfiguration config,
    IQueryRunner queryRunner) : IRequestComposer, IPreviewRequestComposer
{
    /// <summary>What replaces a granted value the previewing caller may not read.</summary>
    private const string MaskText = "***";

    /// <summary>The granted fields a preview masks, and whether it masks the rows of the query too.</summary>
    private sealed record Mask(HashSet<string> Fields, bool Query)
    {
        public static readonly Mask None = new(new(StringComparer.OrdinalIgnoreCase), false);
    }

    /// <summary>Sent when a connector names no header of its own. See <see cref="IdempotencyHeaderName"/>.</summary>
    private const string DefaultIdempotencyHeader = "Idempotency-Key";

    public Task<ComposedRequest> ComposeAsync(
        RequestDefinition definition, Connector connector, Content content, string? idempotencyKey, CancellationToken ct) =>
        ComposeGuardedAsync(definition, connector, content, idempotencyKey, null, ct);

    public Task<ComposedRequest> PreviewAsync(
        RequestDefinition definition, Connector connector, Content content, FieldReadCheck mayRead, CancellationToken ct) =>
        ComposeGuardedAsync(definition, connector, content, null, mayRead, ct);

    private async Task<ComposedRequest> ComposeGuardedAsync(
        RequestDefinition definition, Connector connector, Content content, string? idempotencyKey,
        FieldReadCheck? mayRead, CancellationToken ct)
    {
        try
        {
            return await ComposeCoreAsync(definition, connector, content, idempotencyKey, mayRead, ct);
        }
        catch (TemplateScanException ex)
        {
            // A template that cannot be read in full is refused, never sent on a partial read.
            return ComposedRequest.Refused(ex.Message);
        }
    }

    private async Task<ComposedRequest> ComposeCoreAsync(
        RequestDefinition definition, Connector connector, Content content, string? idempotencyKey,
        FieldReadCheck? mayRead, CancellationToken ct)
    {
        // The account's definition wins over a tenant's own of the same name, the same lookup the
        // sensitivity service makes, so the level judged here is the level a read would judge.
        var schema = await AccountContentTypes.ResolveAsync(session, content.ContentType, ct);

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

        // A template over the length cap is refused whole, so no check below reads it in part.
        foreach (var template in templates) TemplateHoles.Guard(template);

        // Resolved once, before either refusal check, so both a Sensitive field and a bad query hole
        // are checked against the same set of templates and neither is composed while the other
        // could still be the reason nothing should be sent.
        var grants = ConnectorGrants.SetOf(connector.AllowedFields);
        var query = await ResolveQueryAsync(definition, templates, grants, mayRead, ct);
        if (query.Refusal is not null) return ComposedRequest.Refused(query.Refusal);

        var refusal = Refuse(templates, schema, grants, content);
        if (refusal is not null) return ComposedRequest.Refused(refusal);

        var mask = await MaskAsync(templates, schema, grants, content, query, mayRead);

        // Path and headers are not JSON, whatever the body is. Escaping a path segment as if it were
        // a JSON string would put backslashes in a URL.
        var path = Substitute(definition.PathTemplate, content, query.Rows, Escaping.Url, mask);

        // A value of ".." is unreserved, so escaping leaves it, and the address then collapses it
        // into the segment before. A dot segment the template did not write is a value's, and refused.
        if (ValueMakesDotSegment(definition.PathTemplate, path))
        {
            return ComposedRequest.Refused("A value in the path is a dot segment, which would change which endpoint is addressed.");
        }

        var (headers, headerRefusal) = BuildHeaders(definition, connector, content, query.Rows, idempotencyKey, mask);
        if (headerRefusal is not null) return ComposedRequest.Refused(headerRefusal);

        var (body, bodyRefusal) = BuildBody(definition, content, query.Rows, mask);
        if (bodyRefusal is not null) return ComposedRequest.Refused(bodyRefusal);

        var composed = Address(definition, connector, path, headers, body);
        return composed.Ok
            ? composed with { RedactedHeaders = RedactedHeaderNames(definition, schema, grants, query) }
            : composed;
    }

    /// <summary>
    /// The granted fields a preview may not show. Empty for a send, which has no reader to ask: what
    /// the connector is granted is what it sends.
    /// </summary>
    private static async Task<Mask> MaskAsync(
        List<string> templates, ContentTypeDefinition schema, ISet<string> grants, Content content,
        QueryContext query, FieldReadCheck? mayRead)
    {
        if (mayRead is null) return Mask.None;

        var masked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in templates.SelectMany(t => ConnectorGrants.GrantedReads(t, schema, grants)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var field = schema.Fields.First(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
            if (!await mayRead(schema.Name, field, content)) masked.Add(field.Name);
        }

        return new Mask(masked, query.Unreadable);
    }

    /// <summary>The headers whose template reads a granted field, or the rows of a query that did.</summary>
    private static List<string> RedactedHeaderNames(
        RequestDefinition definition, ContentTypeDefinition schema, ISet<string> grants, QueryContext query) =>
        definition.HeaderTemplates
            .Where(h => ConnectorGrants.GrantedReads(h.Value, schema, grants).Count > 0
                || (query.Granted && TemplateHoles.Scan(h.Value).Any(
                    x => x.Reads.Any(r => r.StartsWith("query.", StringComparison.OrdinalIgnoreCase)))))
            .Select(h => h.Key)
            .ToList();

    /// <summary>
    /// Whether a segment of the composed path is a dot segment where the template's segment at the
    /// same place is not one. A value's <c>/</c>, <c>?</c> and <c>#</c> are escaped, so the two
    /// lists line up. When they do not and the composed path holds a dot segment, it is refused.
    /// </summary>
    private static bool ValueMakesDotSegment(string template, string composed)
    {
        var written = Segments(template);
        var sent = Segments(composed);

        if (!sent.Any(IsDotSegment)) return false;
        if (written.Length != sent.Length) return true;

        return sent.Where((segment, at) => IsDotSegment(segment) && !IsDotSegment(written[at])).Any();
    }

    private static string[] Segments(string path)
    {
        var end = path.AsSpan().IndexOfAny('?', '#');
        return (end < 0 ? path : path[..end]).Split('/');
    }

    private static bool IsDotSegment(string segment) =>
        segment.Replace("%2e", ".", StringComparison.OrdinalIgnoreCase) is "." or "..";

    private (Dictionary<string, string> Headers, string? Refusal) BuildHeaders(
        RequestDefinition definition, Connector connector, Content content,
        IReadOnlyList<Dictionary<string, object>> rows, string? idempotencyKey, Mask mask)
    {
        var headers = new Dictionary<string, string>(definition.HeaderTemplates.Count);
        foreach (var (name, template) in definition.HeaderTemplates)
        {
            var value = Substitute(template, content, rows, Escaping.None, mask);

            // Refused rather than stripped. A header has no quoting to fall back to the way a JSON
            // body does, so a value here reaches the sender's TryAddWithoutValidation exactly as
            // composed, and "safe\r\nX-Injected: evil" becomes a second header on a call sent with
            // the connector's own credentials attached. Stripping the control character would send
            // a request the operator did not write, silently, which is the same reason a Sensitive
            // field is refused rather than masked.
            if (HasControlCharacter(value))
            {
                return (headers,
                    $"Header '{name}' cannot be composed: a value in it contains a control character, "
                    + "which could add another header to this request.");
            }

            headers[name] = value;
        }

        // The runner's key, unchanged: WebhookAction sends the same value verbatim, and a receiver
        // comparing the two paths must see one call, not a derivative of it. A template that already
        // named this exact header is left alone rather than overwritten, since the operator wrote
        // that one on purpose. A missing key means this call has no run behind it (a dry run, a
        // test), and composing still proceeds without the header rather than being refused for a
        // protection only the runner can supply.
        var idempotencyHeader = IdempotencyHeaderName(connector);
        if (idempotencyHeader is not null
            && !string.IsNullOrWhiteSpace(idempotencyKey)
            && !headers.Keys.Any(k => string.Equals(k, idempotencyHeader, StringComparison.OrdinalIgnoreCase)))
        {
            // The same check every templated header value already passes. Nothing but the runner is
            // expected to supply this value, but a header value has no quoting to fall back on, so it
            // is checked rather than trusted.
            if (HasControlCharacter(idempotencyKey))
            {
                return (headers, $"'{idempotencyHeader}' cannot be composed: the idempotency key contains a control character.");
            }

            headers[idempotencyHeader] = idempotencyKey;
        }

        return (headers, null);
    }

    private (string? Body, string? Refusal) BuildBody(
        RequestDefinition definition, Content content, IReadOnlyList<Dictionary<string, object>> rows, Mask mask)
    {
        var isJson = definition.BodyContentType.Contains("json", StringComparison.OrdinalIgnoreCase);
        var body = definition.BodyTemplate is null
            ? null
            : Substitute(definition.BodyTemplate, content, rows, isJson ? Escaping.Json : Escaping.None, mask);

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
                return (body, $"The composed body is not valid JSON: {ex.Message}");
            }
        }

        return (body, null);
    }

    private static ComposedRequest Address(
        RequestDefinition definition, Connector connector, string path, Dictionary<string, string> headers, string? body)
    {
        if (!Uri.TryCreate(connector.BaseUrl, UriKind.Absolute, out var baseUri))
        {
            return ComposedRequest.Refused($"Connector '{connector.Slug}' has no absolute base URL.");
        }

        if (!Uri.TryCreate(baseUri, path, out var url))
        {
            return ComposedRequest.Refused($"'{path}' does not combine with the connector's base URL.");
        }

        // The connector's credentials go on whatever is composed here, so a path written as an
        // absolute or scheme-relative URL must not take them to a host they were not entered for.
        if (ConnectorOrigin.Of(url) != ConnectorOrigin.Of(baseUri))
        {
            return ComposedRequest.Refused(
                "The path addresses a different scheme, host or port from the connector's base URL. "
                + "Write it relative to the base URL.");
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
    private static string? Refuse(
        IEnumerable<string> templates, ContentTypeDefinition schema, ISet<string> grants, Content content)
    {
        var found = ConnectorGrants.FirstRefused(templates, schema, grants);
        if (found is not null)
        {
            var field = found.Value.Field;
            var grantable = field.Sensitivity is SensitivityLevel.Sensitive or SensitivityLevel.Members
                && !barakoCMS.Core.Validation.PublicFields.IsOwnerHidden(field);

            return $"'{found.Value.Hole}' is {field.Sensitivity} on '{schema.Name}', and this request "
                 + "would send it to a third party. A field that is not Public cannot leave, "
                 + "even when a template names it."
                 + (grantable ? " A connector can be granted it with allowedFields." : string.Empty);
        }

        // A request renders plain and formatted holes only. Sending the other forms as written would
        // put the author's formula in front of the third party, so they are refused, and a granted
        // field does not change that: its hole passes the check above and is stopped here.
        var beyond = TemplateHoles.FirstBeyondPlain(templates);
        if (beyond is not null)
        {
            return $"'{beyond}' is an expression, a loop or a dataset row, which a request does not render, so it would be sent as written. Use plain holes.";
        }

        // A granted field never leaves from an entry whose own level is Hidden, the rule a webhook
        // keeps for the same reason.
        if (content.Sensitivity == SensitivityLevel.Hidden
            && templates.Any(t => ConnectorGrants.GrantedReads(t, schema, grants).Count > 0))
        {
            return "This entry is Hidden, so a granted field is not sent from it.";
        }

        return null;
    }

    /// <summary>The rows a <c>{{query.*}}</c> hole may draw from, or the reason none may run.</summary>
    /// <param name="Granted">The query reads a field the connector was granted.</param>
    /// <param name="Unreadable">A preview's reader may not read one of those fields, so its rows are masked.</param>
    private readonly record struct QueryContext(
        IReadOnlyList<Dictionary<string, object>> Rows, string? Refusal, bool Granted = false, bool Unreadable = false)
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
        RequestDefinition definition, List<string> templates, ISet<string> grants, FieldReadCheck? mayRead, CancellationToken ct)
    {
        var names = new List<string>();
        foreach (var template in templates)
        {
            foreach (var scanned in TemplateHoles.Scan(template).Where(h => h.Form == HoleForm.Plain))
            {
                var name = scanned.Reads[0];
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

        var queryDefinition = await session.Query<QueryDefinition>()
            .FirstOrDefaultAsync(q => q.Slug == definition.QuerySlug && q.DeletedAt == null, ct);

        if (queryDefinition is null)
        {
            return new QueryContext([], $"'{hole}' needs a query, and '{definition.QuerySlug}' does not exist.");
        }

        var result = await RunQueryAsync(queryDefinition, grants, ct);
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

        // A scalar hole with no rows to draw from is refused rather than composed as an empty
        // string. Composing it would make "the query matched nothing" and "the field is genuinely
        // empty" produce the identical value, and there is no way for whoever reads the sent request
        // to tell those apart afterwards. {{query.rows}} does not need this: an empty array is a
        // real, distinguishable answer to "how many rows matched", so it is excluded above.
        if (result.Rows.Count == 0)
        {
            foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var field = name["query.".Length..];
                if (string.Equals(field, "rows", StringComparison.OrdinalIgnoreCase)) continue;

                var thisHole = "{{" + name + "}}";
                return new QueryContext([], $"'{thisHole}' needs a query, and "
                    + $"'{definition.QuerySlug}' matched no rows. A single field has no value to "
                    + "compose when there are no rows: use {{query.rows}} for an empty array instead, "
                    + "or change the query so it matches something.");
            }
        }

        return await WithGrantUseAsync(result.Rows, queryDefinition, grants, mayRead, ct);
    }

    /// <summary>
    /// Runs the query by the connector's grants when it has any: a granted field may be projected,
    /// filtered or sorted on, and a followed reference needs the field granted on the type it points
    /// at. A runner that cannot do that runs the Public-only query, which refuses a field that is not.
    /// </summary>
    private Task<QueryResult> RunQueryAsync(QueryDefinition query, ISet<string> grants, CancellationToken ct) =>
        grants.Count > 0 && queryRunner is IGrantedQueryRunner byGrant
            ? byGrant.RunAsync(query, grants, ct)
            : queryRunner.RunAsync(query, ct);

    /// <summary>
    /// Whether the query reads a field that is not Public, which with grants means a granted one, and
    /// whether a previewing reader may not read all of them. A followed reference counts as one the
    /// reader may not read: the preview has no cheap way to tell, and it fails closed.
    /// </summary>
    private async Task<QueryContext> WithGrantUseAsync(
        IReadOnlyList<Dictionary<string, object>> rows, QueryDefinition query, ISet<string> grants,
        FieldReadCheck? mayRead, CancellationToken ct)
    {
        if (grants.Count == 0) return new QueryContext(rows, null);

        var schema = await AccountContentTypes.ResolveAsync(session, query.ContentType, ct);
        if (schema is null || !ConnectorGrants.QueryReadsNonPublic(query, schema)) return new QueryContext(rows, null);
        if (mayRead is null) return new QueryContext(rows, null, true);

        var entry = new Content { ContentType = schema.Name };
        foreach (var name in ConnectorGrants.NamedFields(query))
        {
            if (name.Contains('.')) return new QueryContext(rows, null, true, true);

            var field = schema.Fields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
            if (field is not null && field.Sensitivity != SensitivityLevel.Public && !await mayRead(schema.Name, field, entry))
            {
                return new QueryContext(rows, null, true, true);
            }
        }

        return new QueryContext(rows, null, true);
    }

    /// <summary>The schema field a variable names, or null when it names a system variable.</summary>
    private static FieldDefinition? FieldFor(string name, ContentTypeDefinition schema) =>
        TemplateHoles.FieldFor(name, schema);

    /// <summary>
    /// The header name that carries the run's idempotency key on this connector, or null when the
    /// connector has it switched off.
    /// </summary>
    /// <remarks>
    /// Lives on <see cref="Connector.Settings"/>, not the request definition: the spelling a
    /// provider wants is a property of the provider, not of one call to it, and every request
    /// definition against the same connector should agree on it without each one repeating the
    /// choice. Unset means the default, <see cref="DefaultIdempotencyHeader"/>, so an operator gets
    /// the protection without asking for it.
    ///
    /// The opt-out is the literal value <c>off</c>, not an empty string. An empty string is what a
    /// text field holds after someone clears it by accident, and that should fall back to the
    /// default rather than silently disable the protection; typing <c>off</c> is deliberate the way
    /// clearing a field is not.
    /// </remarks>
    private static string? IdempotencyHeaderName(Connector connector)
    {
        var configured = connector.Settings.GetValueOrDefault(ConnectorSettingKeys.IdempotencyHeader);
        if (string.IsNullOrWhiteSpace(configured)) return DefaultIdempotencyHeader;

        return string.Equals(configured.Trim(), "off", StringComparison.OrdinalIgnoreCase)
            ? null
            : configured.Trim();
    }

    private enum Escaping { None, Json, Url }

    /// <summary>Whether a composed header value carries anything that is not a printable character.</summary>
    /// <remarks>
    /// Not just <c>\r</c> and <c>\n</c>: <see cref="char.IsControl(char)"/> covers the whole C0 and
    /// C1 range, because a header value has no legitimate reason to carry any of it, and narrowing
    /// the check to the two characters a particular HTTP client happens to split on is how the next
    /// client's parser becomes the next bypass.
    /// </remarks>
    private static bool HasControlCharacter(string value)
    {
        foreach (var c in value)
        {
            if (char.IsControl(c)) return true;
        }

        return false;
    }

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
        string template, Content content, IReadOnlyList<Dictionary<string, object>> queryRows, Escaping escaping, Mask mask)
    {
        return TemplateHoles.Replace(template, (name, format) =>
        {
            if (name.StartsWith("query.", StringComparison.OrdinalIgnoreCase))
            {
                if (format is not null) return null;
                var queryField = name["query.".Length..];
                return mask.Query
                    ? MaskedQuery(queryField, queryRows, escaping)
                    : SubstituteQuery(queryField, queryRows, escaping);
            }

            if (mask.Fields.Count > 0 && mask.Fields.Contains(TemplateHoles.Bare(name).Split('.', 2)[0]))
            {
                return Escape(MaskText, escaping);
            }

            var raw = Value(name, content);
            if (raw is not null && format is not null) raw = TemplateExpression.ApplyFormat(format, raw, TemplateContext.Unprepared);
            if (raw is null) return null;

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
    /// anything that is not "rows" or one of the query's own fields, and a scalar field when there
    /// were no rows to read one from, so this only has to produce it.
    /// </summary>
    /// <remarks>
    /// <c>rows</c> is the one case not re-escaped as a JSON string in a JSON body: it is already a
    /// JSON array, produced by the same projection the preview endpoint returns, and JSON-encoding
    /// that array as a string would hand the recipient a string full of JSON rather than an array. A
    /// scalar field is read from the first row, which <see cref="ResolveQueryAsync"/> guarantees
    /// exists by this point, and treated exactly like a content field: an empty string means the
    /// field itself was empty on that row, not that the query matched nothing, because that
    /// ambiguity is exactly what the caller already refused to compose.
    /// </remarks>
    private static string Escape(string value, Escaping escaping) => escaping switch
    {
        Escaping.Json => JsonEncodedText.Encode(value).ToString(),
        Escaping.Url => Uri.EscapeDataString(value),
        _ => value,
    };

    /// <summary>
    /// A query hole the previewer may not read. The rows keep their shape, one object per row with
    /// each value masked, so a body that is JSON stays JSON and the preview mirrors the send.
    /// </summary>
    private static string MaskedQuery(string field, IReadOnlyList<Dictionary<string, object>> rows, Escaping escaping)
    {
        if (!string.Equals(field, "rows", StringComparison.OrdinalIgnoreCase)) return Escape(MaskText, escaping);

        var masked = rows.Select(r => r.Keys.ToDictionary(k => k, _ => (object)MaskText)).ToList();
        return SubstituteQuery("rows", masked, escaping);
    }

    private static string SubstituteQuery(
        string field, IReadOnlyList<Dictionary<string, object>> rows, Escaping escaping)
    {
        if (string.Equals(field, "rows", StringComparison.OrdinalIgnoreCase))
        {
            var json = JsonSerializer.Serialize(rows);
            return escaping == Escaping.Url ? Uri.EscapeDataString(json) : json;
        }

        var value = FieldValue(rows[0], field);
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
                var baseUrl = config[CanonicalHost.BaseUrlKey]?.Trim().TrimEnd('/');
                if (string.IsNullOrEmpty(baseUrl)) return null;

                var slug = content.Data.TryGetValue("Slug", out var s) ? s?.ToString() : null;
                return string.IsNullOrWhiteSpace(slug)
                    ? $"{baseUrl}/api/public/{content.ContentType}/{content.Id}"
                    : $"{baseUrl}/api/public/{content.ContentType}/{slug}";
            }
        }

        var bare = TemplateHoles.Bare(name);

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
