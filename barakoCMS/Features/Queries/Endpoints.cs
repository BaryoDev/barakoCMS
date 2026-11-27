using barakoCMS.Infrastructure;
using barakoCMS.Infrastructure.Audit;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Connectors;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace barakoCMS.Features.Queries;

internal sealed class QueryResponse
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Slug { get; init; } = string.Empty;
    public string ContentType { get; init; } = string.Empty;
    public List<QueryFilter> Filters { get; init; } = new();
    public string? SortField { get; init; }
    public bool Descending { get; init; }
    public int Limit { get; init; }
    public List<string> Fields { get; init; } = new();
    public QueryRange? Range { get; init; }
    public List<QueryGroup>? GroupBy { get; init; }
    public List<QueryMeasure>? Measures { get; init; }

    /// <summary>The query as text, in the one form a save of it reads back as the same query.</summary>
    public string Text { get; init; } = string.Empty;

    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }

    /// <summary>The text of a stored query. An old one that has no text form reads as empty and still lists.</summary>
    private static string TextOf(QueryDefinition q)
    {
        try { return barakoCMS.Infrastructure.Connectors.QueryText.Print(q); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or NullReferenceException) { return string.Empty; }
    }

    public static QueryResponse From(QueryDefinition q) => new()
    {
        Text = TextOf(q),
        Id = q.Id,
        Name = q.Name,
        Slug = q.Slug,
        ContentType = q.ContentType,
        Filters = q.Filters,
        SortField = q.SortField,
        Descending = q.Descending,
        Limit = q.Limit,
        Fields = q.Fields,
        Range = q.Range,
        GroupBy = q.GroupBy,
        Measures = q.Measures,
        CreatedAt = q.CreatedAt,
        UpdatedAt = q.UpdatedAt,
    };
}

internal class SaveQueryRequest
{
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public List<QueryFilter> Filters { get; set; } = new();
    public string? SortField { get; set; }
    public bool Descending { get; set; }
    public int Limit { get; set; } = QueryDefinition.DefaultLimit;
    public List<string> Fields { get; set; } = new();
    public QueryRange? Range { get; set; }
    public List<QueryGroup>? GroupBy { get; set; }
    public List<QueryMeasure>? Measures { get; set; }

    /// <summary>
    /// The query written as text, for example <c>from student where Number = @data.StudentNo select Email limit 1</c>.
    /// Given instead of the content type, filters, sort, fields, limit, range, groups and measures,
    /// and never with them.
    /// </summary>
    public string? Text { get; set; }
}

/// <summary>A preview of a saved query: the values its parameters take, when it has any.</summary>
internal sealed class PreviewQueryRequest
{
    /// <summary>The value of each parameter by its name without the at sign, for example <c>data.StudentNo</c>.</summary>
    public Dictionary<string, string>? Parameters { get; set; }
}

internal static class PreviewBody
{
    private const int MaxBytes = 16 * 1024;
    private const int MaxParameters = 50;

    /// <summary>The preview's optional body, or one sentence saying why it cannot be read. Nothing the caller sent is echoed.</summary>
    public static async Task<(PreviewQueryRequest Body, string? Problem)> ReadAsync(HttpRequest request, CancellationToken ct)
    {
        var none = new PreviewQueryRequest();
        if (request.ContentLength == 0) return (none, null);

        var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxBytes) return (none, $"The body is longer than {MaxBytes} bytes.");
            buffer.Write(chunk, 0, read);
        }

        var bytes = buffer.ToArray();
        if (bytes.All(b => b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')) return (none, null);

        try
        {
            var body = System.Text.Json.JsonSerializer.Deserialize<PreviewQueryRequest>(
                bytes, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (body is null) return (none, null);
            if (body.Parameters is { Count: > MaxParameters }) return (none, $"A preview takes at most {MaxParameters} parameters.");
            return (body, null);
        }
        catch (System.Text.Json.JsonException)
        {
            return (none, "The body is not JSON of the form { \"parameters\": { \"name\": \"value\" } }.");
        }
    }
}

internal static class QueryForms
{
    /// <summary>
    /// Whether a query uses what only a step's run reads: a parameter, a system field or a followed
    /// reference. Every other query is read exactly as it always was.
    /// </summary>
    public static bool NeedsAContext(QueryDefinition q) =>
        q.Filters.Any(f => f.Param is not null || Array.IndexOf(barakoCMS.Infrastructure.Connectors.QueryText.SystemFields, f.Field) >= 0)
        || (q.SortField is not null && Array.IndexOf(barakoCMS.Infrastructure.Connectors.QueryText.SystemFields, q.SortField) >= 0)
        || q.Fields.Any(f => f.Contains('.') || Array.IndexOf(barakoCMS.Infrastructure.Connectors.QueryText.SystemFields, f) >= 0);

    public static Guid UserIdOf(System.Security.Claims.ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirst("UserId")?.Value, out var id) ? id : Guid.Empty;
}

internal sealed class QueryPreviewResponse
{
    public bool Ok { get; init; }
    public string? Refusal { get; init; }
    public int Count { get; init; }
    public IReadOnlyList<Dictionary<string, object>> Rows { get; init; } = [];

    /// <summary>Every measure over the whole set, for an aggregate query. Left out otherwise.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, object?>? Totals { get; init; }
}

internal static class QueryGate
{
    /// <summary>
    /// The names that gated queries before <see cref="SystemCapabilities.ManageQueries"/>, kept as
    /// the legacy fallback so an upgrade does not lock a deployment out.
    /// </summary>
    internal static readonly IReadOnlyList<string> LegacyRoles = CapabilityGate.AdminLegacyRoles;

}

/// <summary>A query list request. <c>deleted=true</c> lists the deleted queries instead of the live ones.</summary>
internal sealed class ListQueriesRequest : ListRequest
{
    public bool? Deleted { get; set; }
}

internal sealed class ListQueriesEndpoint(
    IQuerySession session) : Endpoint<ListQueriesRequest, PaginatedResponse<QueryResponse>>
{
    public override void Configure()
    {
        Get("/api/queries");
        Definition.RequireCapability(SystemCapabilities.ManageQueries, QueryGate.LegacyRoles);
    }

    public override async Task HandleAsync(ListQueriesRequest req, CancellationToken ct)
    {
        var all = session.Query<QueryDefinition>();
        var wanted = req.Deleted == true ? all.Where(q => q.DeletedAt != null) : all.Where(q => q.DeletedAt == null);
        var page = await wanted
            .OrderBy(q => q.Name)
            .ToPagedResponseAsync(req, ct);

        await Send.ResponseAsync(page.Map(QueryResponse.From), cancellation: ct);
    }
}

internal sealed class GetQueryEndpoint(IQuerySession session) : EndpointWithoutRequest<QueryResponse>
{
    public override void Configure()
    {
        Get("/api/queries/{slug}");
        Definition.RequireCapability(SystemCapabilities.ManageQueries, QueryGate.LegacyRoles);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var slug = Route<string>("slug") ?? string.Empty;

        if (!Slugs.IsValid(slug))
        {
            ThrowError("That is not a query slug: lowercase letters, digits and hyphens only.", 400);
            return;
        }

        var found = await session.Query<QueryDefinition>().FirstOrDefaultAsync(q => q.Slug == slug && q.DeletedAt == null, ct);
        if (found is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.ResponseAsync(QueryResponse.From(found), cancellation: ct);
    }
}

internal sealed class SaveQueryEndpoint(
    IDocumentSession session,
    IQueryRunner runner,
    IContextQueryRunner contextRunner,
    barakoCMS.Infrastructure.Services.IPermissionResolver permissions,
    barakoCMS.Core.Interfaces.ISensitivityService sensitivity,
    barakoCMS.Infrastructure.Multitenancy.TenantContext tenant) : Endpoint<SaveQueryRequest, QueryResponse>
{
    public override void Configure()
    {
        Post("/api/queries");
        Definition.RequireCapability(SystemCapabilities.ManageQueries, QueryGate.LegacyRoles);
    }

    private static string? ShapeRefusal(SaveQueryRequest req)
    {
        if (req.Measures is { Count: > 0 })
        {
            if (req.Fields is { Count: > 0 }) return "An aggregate query returns figures, not fields. Remove fields.";
            if (!string.IsNullOrWhiteSpace(req.SortField)) return "An aggregate query is ordered by its groups. Remove the sort field.";
            if (req.Limit != QueryDefinition.DefaultLimit) return "An aggregate query is capped by its groups, not a limit. Remove the limit.";
            return null;
        }

        if (req.Range is not null) return "A range only applies to an aggregate query. Add a measure or remove the range.";
        if (req.GroupBy is { Count: > 0 }) return "Grouping only applies to an aggregate query. Add a measure or remove the grouping.";
        return null;
    }

    /// <summary>Fills the members from the text, or says why it cannot be read or why it was given with them.</summary>
    private static string? ReadText(SaveQueryRequest req)
    {
        if (req.Text is null) return null;

        if (!string.IsNullOrWhiteSpace(req.ContentType) || req.Filters is { Count: > 0 } || req.Fields is { Count: > 0 }
            || !string.IsNullOrWhiteSpace(req.SortField) || req.Range is not null || req.GroupBy is { Count: > 0 }
            || req.Measures is { Count: > 0 })
        {
            return "Give the query as text or as its parts, not both.";
        }

        var read = barakoCMS.Infrastructure.Connectors.QueryText.Parse(req.Text);
        if (read.Definition is not { } parsed) return read.Error;

        req.ContentType = parsed.ContentType;
        req.Filters = parsed.Filters;
        req.SortField = parsed.SortField;
        req.Descending = parsed.Descending;
        req.Limit = parsed.Limit;
        req.Fields = parsed.Fields;
        req.Range = parsed.Range;
        req.GroupBy = parsed.GroupBy;
        req.Measures = parsed.Measures;
        return null;
    }

    public override async Task HandleAsync(SaveQueryRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Name)) { ThrowError("Name is required.", 400); return; }

        var textRefusal = ReadText(req);
        if (textRefusal is not null) { ThrowError(textRefusal, 400); return; }
        if (!Slugs.IsValid(req.Slug))
        {
            ThrowError("Slug must be lowercase letters, digits and hyphens.", 400);
            return;
        }

        var shapeRefusal = ShapeRefusal(req);
        if (shapeRefusal is not null) { ThrowError(shapeRefusal, 400); return; }

        var existing = await session.Query<QueryDefinition>().FirstOrDefaultAsync(q => q.Slug == req.Slug, ct);
        // A deleted query still holds the slug. Saving over it reuses the row, so creating after a
        // delete works as it always did, and the old definition is gone for good.
        var replacesDeleted = existing?.DeletedAt is not null;
        if (replacesDeleted)
        {
            existing!.CreatedAt = DateTime.UtcNow;
            existing.DeletedAt = null;
        }
        var definition = existing ?? new QueryDefinition { Id = Guid.NewGuid(), Slug = req.Slug.ToLowerInvariant() };

        definition.Name = req.Name.Trim();
        definition.ContentType = req.ContentType.Trim();
        definition.Filters = req.Filters;
        definition.SortField = req.SortField;
        definition.Descending = req.Descending;
        definition.Limit = req.Limit;
        definition.Fields = req.Fields ?? [];
        definition.Range = req.Range;
        definition.GroupBy = req.GroupBy;
        definition.Measures = req.Measures;
        definition.UpdatedAt = DateTime.UtcNow;

        // Validated against the schema before it is stored, so an unknown or non-Public field is a
        // 400 while the operator is still looking at the form, rather than a workflow that fails
        // later with a message about a field they thought they had.
        // An aggregate is checked against what this caller may read, since its figures are computed
        // under the reader's rules, and returns no rows for a projection to name.
        var refusal = definition.IsAggregate
            ? (await new Run.AggregateQuery(session, permissions, sensitivity)
                .RunAsync(definition, User, null, null, execute: false, ct)).Refusal
            : QueryForms.NeedsAContext(definition)
                // Judged for whoever saves it, the same way a step reads, so a query that names a
                // field its author may not read is refused here and not at the first run.
                ? await contextRunner.ValidateAsync(
                    definition, new QueryRunContext(null) { AsActor = true, Actor = QueryForms.UserIdOf(User) }, ct)
                : await runner.ValidateAsync(definition, ct);
        if (refusal is not null)
        {
            ThrowError(refusal, 400);
            return;
        }

        session.Store(definition);

        var actorId = ActingCaller.IdOf(User);
        await AuditLog.RecordAsync(session, tenant.Slug,
            existing is null || replacesDeleted ? "query.created" : "query.updated",
            actorId, User.FindFirst("Username")?.Value,
            targetType: nameof(QueryDefinition), targetId: definition.Id.ToString(),
            metadata: new Dictionary<string, object>
            {
                ["slug"] = definition.Slug,
                ["contentType"] = definition.ContentType,
                // What leaves, which is the part a review asks about.
                ["fields"] = string.Join(", ", definition.Fields),
                ["limit"] = definition.Limit,
            }, ct: ct);

        await session.SaveChangesAsync(ct);

        await Send.ResponseAsync(QueryResponse.From(definition), cancellation: ct);
    }
}

internal sealed class DeleteQueryEndpoint(
    IDocumentSession session,
    barakoCMS.Infrastructure.Multitenancy.TenantContext tenant) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Delete("/api/queries/{slug}");
        Definition.RequireCapability(SystemCapabilities.ManageQueries, QueryGate.LegacyRoles);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var slug = Route<string>("slug") ?? string.Empty;

        if (!Slugs.IsValid(slug))
        {
            ThrowError("That is not a query slug: lowercase letters, digits and hyphens only.", 400);
            return;
        }

        var found = await session.Query<QueryDefinition>().FirstOrDefaultAsync(q => q.Slug == slug && q.DeletedAt == null, ct);
        if (found is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        found.DeletedAt = DateTime.UtcNow;
        session.Store(found);

        var actorId = ActingCaller.IdOf(User);
        await AuditLog.RecordAsync(session, tenant.Slug, "query.deleted", actorId,
            User.FindFirst("Username")?.Value,
            targetType: nameof(QueryDefinition), targetId: found.Id.ToString(),
            metadata: new Dictionary<string, object> { ["slug"] = found.Slug }, ct: ct);

        await session.SaveChangesAsync(ct);
        await Send.NoContentAsync(ct);
    }
}

internal sealed class RestoreQueryEndpoint(
    IDocumentSession session,
    barakoCMS.Infrastructure.Multitenancy.TenantContext tenant) : EndpointWithoutRequest<QueryResponse>
{
    public override void Configure()
    {
        Post("/api/queries/{slug}/restore");
        Definition.RequireCapability(SystemCapabilities.ManageQueries, QueryGate.LegacyRoles);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var slug = Route<string>("slug") ?? string.Empty;
        if (!Slugs.IsValid(slug))
        {
            ThrowError("That is not a query slug: lowercase letters, digits and hyphens only.", 400);
            return;
        }

        var found = await session.Query<QueryDefinition>()
            .FirstOrDefaultAsync(q => q.Slug == slug && q.DeletedAt != null, ct);
        if (found is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        found.DeletedAt = null;
        found.UpdatedAt = DateTime.UtcNow;
        session.Store(found);

        var actorId = ActingCaller.IdOf(User);
        await AuditLog.RecordAsync(session, tenant.Slug, "query.restored", actorId,
            User.FindFirst("Username")?.Value,
            targetType: nameof(QueryDefinition), targetId: found.Id.ToString(),
            metadata: new Dictionary<string, object> { ["slug"] = found.Slug }, ct: ct);

        await session.SaveChangesAsync(ct);
        await Send.ResponseAsync(QueryResponse.From(found), cancellation: ct);
    }
}

/// <summary>
/// Removes a deleted query for good. Mirrors content's archive versus erase split. It only takes a
/// query that is already deleted, so a live one cannot be destroyed in one call.
/// </summary>
internal sealed class EraseQueryEndpoint(
    IDocumentSession session,
    barakoCMS.Infrastructure.Multitenancy.TenantContext tenant) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Delete("/api/queries/{slug}/erase");
        Definition.RequireCapability(SystemCapabilities.ManageQueries, QueryGate.LegacyRoles);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var slug = Route<string>("slug") ?? string.Empty;
        if (!Slugs.IsValid(slug))
        {
            ThrowError("That is not a query slug: lowercase letters, digits and hyphens only.", 400);
            return;
        }

        var found = await session.Query<QueryDefinition>()
            .FirstOrDefaultAsync(q => q.Slug == slug && q.DeletedAt != null, ct);
        if (found is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        session.Delete(found);

        var actorId = ActingCaller.IdOf(User);
        await AuditLog.RecordAsync(session, tenant.Slug, "query.erased", actorId,
            User.FindFirst("Username")?.Value,
            targetType: nameof(QueryDefinition), targetId: found.Id.ToString(),
            metadata: new Dictionary<string, object> { ["slug"] = found.Slug }, ct: ct);

        await session.SaveChangesAsync(ct);
        await Send.NoContentAsync(ct);
    }
}

/// <summary>
/// Runs a saved query and returns the rows, so an operator can see what a payload would carry.
/// </summary>
/// <remarks>
/// The same shape as the request dry run and for the same reason: a query is written against a
/// schema and its rows are read by a third party, and the gap between those is where a recipient
/// list is wrong in ways nobody sees until it is sent.
/// </remarks>
internal sealed class PreviewQueryEndpoint(
    IQuerySession session,
    IQueryRunner runner,
    IContextQueryRunner contextRunner,
    barakoCMS.Infrastructure.Services.IPermissionResolver permissions,
    barakoCMS.Core.Interfaces.ISensitivityService sensitivity) : EndpointWithoutRequest<QueryPreviewResponse>
{
    public override void Configure()
    {
        Post("/api/queries/{slug}/preview");
        Definition.RequireCapability(SystemCapabilities.ManageQueries, QueryGate.LegacyRoles);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var slug = Route<string>("slug") ?? string.Empty;

        if (!Slugs.IsValid(slug))
        {
            ThrowError("That is not a query slug: lowercase letters, digits and hyphens only.", 400);
            return;
        }

        // The body is optional and the route took none before it could. It is read here and not
        // bound, because a bound body makes a request with no content type a 415, which would break
        // every caller that sends none. No body, an empty one and a body with any content type read
        // the same as they always did.
        var (req, bodyProblem) = await PreviewBody.ReadAsync(HttpContext.Request, ct);
        if (bodyProblem is not null)
        {
            ThrowError(bodyProblem, 400);
            return;
        }

        var definition = await session.Query<QueryDefinition>().FirstOrDefaultAsync(q => q.Slug == slug && q.DeletedAt == null, ct);
        if (definition is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (!definition.IsAggregate && (QueryForms.NeedsAContext(definition) || req.Parameters is { Count: > 0 }))
        {
            // The values are the ones the caller typed, bound as values. The rows are read as the
            // caller, the way a step reads as the user behind its event.
            var shown = await contextRunner.RunAsync(
                definition,
                new QueryRunContext(req.Parameters) { AsActor = true, Actor = QueryForms.UserIdOf(User) }, ct);

            await Send.ResponseAsync(new QueryPreviewResponse
            {
                Ok = shown.Ok,
                Refusal = shown.Refusal,
                Count = shown.Count,
                Rows = shown.Rows,
            }, cancellation: ct);
            return;
        }

        if (definition.IsAggregate)
        {
            var figures = await new Run.AggregateQuery(session, permissions, sensitivity)
                .RunAsync(definition, User, null, null, execute: true, ct);

            await Send.ResponseAsync(new QueryPreviewResponse
            {
                Ok = figures.Ok,
                Refusal = figures.Refusal,
                Count = figures.Rows.Count,
                Rows = figures.Rows.Select(r => r.ToDictionary(kv => kv.Key, kv => kv.Value!)).ToList(),
                Totals = figures.Ok ? figures.Totals : null,
            }, cancellation: ct);
            return;
        }

        var result = await runner.RunAsync(definition, ct);

        await Send.ResponseAsync(new QueryPreviewResponse
        {
            Ok = result.Ok,
            Refusal = result.Refusal,
            Count = result.Count,
            Rows = result.Rows,
        }, cancellation: ct);
    }
}
