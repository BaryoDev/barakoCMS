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
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }

    public static QueryResponse From(QueryDefinition q) => new()
    {
        Id = q.Id,
        Name = q.Name,
        Slug = q.Slug,
        ContentType = q.ContentType,
        Filters = q.Filters,
        SortField = q.SortField,
        Descending = q.Descending,
        Limit = q.Limit,
        Fields = q.Fields,
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
}

internal sealed class QueryPreviewResponse
{
    public bool Ok { get; init; }
    public string? Refusal { get; init; }
    public int Count { get; init; }
    public IReadOnlyList<Dictionary<string, object>> Rows { get; init; } = [];
}

internal static class QueryGate
{
    /// <summary>
    /// The names that gated queries before <see cref="SystemCapabilities.ManageQueries"/>, kept as
    /// the legacy fallback so an upgrade does not lock a deployment out.
    /// </summary>
    internal static readonly string[] LegacyRoles = ["SuperAdmin", "Admin"];

    internal static bool IsSlug(string value) =>
        System.Text.RegularExpressions.Regex.IsMatch(value, "^[a-z0-9][a-z0-9-]{0,62}$");
}

internal sealed class ListQueriesEndpoint : Endpoint<ListRequest, PaginatedResponse<QueryResponse>>
{
    private readonly IQuerySession _session;

    public ListQueriesEndpoint(IQuerySession session) => _session = session;

    public override void Configure()
    {
        Get("/api/queries");
        Definition.RequireCapability(SystemCapabilities.ManageQueries, QueryGate.LegacyRoles);
    }

    public override async Task HandleAsync(ListRequest req, CancellationToken ct)
    {
        var page = await _session.Query<QueryDefinition>().OrderBy(q => q.Name).ToPagedResponseAsync(req, ct);

        await Send.ResponseAsync(new PaginatedResponse<QueryResponse>
        {
            Items = page.Items.Select(QueryResponse.From).ToList(),
            Page = page.Page,
            PageSize = page.PageSize,
            TotalItems = page.TotalItems,
        }, cancellation: ct);
    }
}

internal sealed class GetQueryEndpoint : EndpointWithoutRequest<QueryResponse>
{
    private readonly IQuerySession _session;

    public GetQueryEndpoint(IQuerySession session) => _session = session;

    public override void Configure()
    {
        Get("/api/queries/{slug}");
        Definition.RequireCapability(SystemCapabilities.ManageQueries, QueryGate.LegacyRoles);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var slug = Route<string>("slug") ?? string.Empty;

        if (!QueryGate.IsSlug(slug))
        {
            ThrowError("That is not a query slug: lowercase letters, digits and hyphens only.", 400);
            return;
        }

        var found = await _session.Query<QueryDefinition>().FirstOrDefaultAsync(q => q.Slug == slug, ct);
        if (found is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.ResponseAsync(QueryResponse.From(found), cancellation: ct);
    }
}

internal sealed class SaveQueryEndpoint : Endpoint<SaveQueryRequest, QueryResponse>
{
    private readonly IDocumentSession _session;
    private readonly IQueryRunner _runner;
    private readonly barakoCMS.Infrastructure.Multitenancy.TenantContext _tenant;

    public SaveQueryEndpoint(
        IDocumentSession session,
        IQueryRunner runner,
        barakoCMS.Infrastructure.Multitenancy.TenantContext tenant)
    {
        _session = session;
        _runner = runner;
        _tenant = tenant;
    }

    public override void Configure()
    {
        Post("/api/queries");
        Definition.RequireCapability(SystemCapabilities.ManageQueries, QueryGate.LegacyRoles);
    }

    public override async Task HandleAsync(SaveQueryRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Name)) { ThrowError("Name is required.", 400); return; }
        if (!QueryGate.IsSlug(req.Slug))
        {
            ThrowError("Slug must be lowercase letters, digits and hyphens.", 400);
            return;
        }

        var existing = await _session.Query<QueryDefinition>().FirstOrDefaultAsync(q => q.Slug == req.Slug, ct);
        var definition = existing ?? new QueryDefinition { Id = Guid.NewGuid(), Slug = req.Slug.ToLowerInvariant() };

        definition.Name = req.Name.Trim();
        definition.ContentType = req.ContentType.Trim();
        definition.Filters = req.Filters;
        definition.SortField = req.SortField;
        definition.Descending = req.Descending;
        definition.Limit = req.Limit;
        definition.Fields = req.Fields;
        definition.UpdatedAt = DateTime.UtcNow;

        // Validated against the schema before it is stored, so an unknown or non-Public field is a
        // 400 while the operator is still looking at the form, rather than a workflow that fails
        // later with a message about a field they thought they had.
        var refusal = await _runner.ValidateAsync(definition, ct);
        if (refusal is not null)
        {
            ThrowError(refusal, 400);
            return;
        }

        _session.Store(definition);

        var actorId = Guid.TryParse(User.FindFirst("UserId")?.Value, out var parsed) ? parsed : (Guid?)null;
        await AuditLog.RecordAsync(_session, _tenant.Slug,
            existing is null ? "query.created" : "query.updated",
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

        await _session.SaveChangesAsync(ct);

        await Send.ResponseAsync(QueryResponse.From(definition), cancellation: ct);
    }
}

internal sealed class DeleteQueryEndpoint : EndpointWithoutRequest
{
    private readonly IDocumentSession _session;
    private readonly barakoCMS.Infrastructure.Multitenancy.TenantContext _tenant;

    public DeleteQueryEndpoint(IDocumentSession session, barakoCMS.Infrastructure.Multitenancy.TenantContext tenant)
    {
        _session = session;
        _tenant = tenant;
    }

    public override void Configure()
    {
        Delete("/api/queries/{slug}");
        Definition.RequireCapability(SystemCapabilities.ManageQueries, QueryGate.LegacyRoles);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var slug = Route<string>("slug") ?? string.Empty;

        if (!QueryGate.IsSlug(slug))
        {
            ThrowError("That is not a query slug: lowercase letters, digits and hyphens only.", 400);
            return;
        }

        var found = await _session.Query<QueryDefinition>().FirstOrDefaultAsync(q => q.Slug == slug, ct);
        if (found is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        _session.Delete(found);

        var actorId = Guid.TryParse(User.FindFirst("UserId")?.Value, out var parsed) ? parsed : (Guid?)null;
        await AuditLog.RecordAsync(_session, _tenant.Slug, "query.deleted", actorId,
            User.FindFirst("Username")?.Value,
            targetType: nameof(QueryDefinition), targetId: found.Id.ToString(),
            metadata: new Dictionary<string, object> { ["slug"] = found.Slug }, ct: ct);

        await _session.SaveChangesAsync(ct);
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
internal sealed class PreviewQueryEndpoint : EndpointWithoutRequest<QueryPreviewResponse>
{
    private readonly IQuerySession _session;
    private readonly IQueryRunner _runner;

    public PreviewQueryEndpoint(IQuerySession session, IQueryRunner runner)
    {
        _session = session;
        _runner = runner;
    }

    public override void Configure()
    {
        Post("/api/queries/{slug}/preview");
        Definition.RequireCapability(SystemCapabilities.ManageQueries, QueryGate.LegacyRoles);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var slug = Route<string>("slug") ?? string.Empty;

        if (!QueryGate.IsSlug(slug))
        {
            ThrowError("That is not a query slug: lowercase letters, digits and hyphens only.", 400);
            return;
        }

        var definition = await _session.Query<QueryDefinition>().FirstOrDefaultAsync(q => q.Slug == slug, ct);
        if (definition is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var result = await _runner.RunAsync(definition, ct);

        await Send.ResponseAsync(new QueryPreviewResponse
        {
            Ok = result.Ok,
            Refusal = result.Refusal,
            Count = result.Count,
            Rows = result.Rows,
        }, cancellation: ct);
    }
}
