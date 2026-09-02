using barakoCMS.Features.Connectors;
using barakoCMS.Infrastructure.Audit;
using barakoCMS.Infrastructure.Connectors;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace barakoCMS.Features.Requests;

/// <summary>A request definition as the API describes it.</summary>
/// <remarks>
/// Every field here is configuration an operator typed. There is no credential anywhere in a request
/// definition, by construction: the connector holds those, encrypted, and they are attached to the
/// finished message after this has composed it.
/// </remarks>
internal sealed class RequestResponse
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Slug { get; init; } = string.Empty;
    public string ConnectorSlug { get; init; } = string.Empty;
    public string Method { get; init; } = "POST";
    public string PathTemplate { get; init; } = string.Empty;
    public Dictionary<string, string> HeaderTemplates { get; init; } = new();
    public string? BodyTemplate { get; init; }
    public string BodyContentType { get; init; } = "application/json";
    public string? QuerySlug { get; init; }
    public string Success { get; init; } = nameof(SuccessRule.TwoHundredRange);
    public string? SuccessJsonPath { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }

    public static RequestResponse From(RequestDefinition r) => new()
    {
        Id = r.Id,
        Name = r.Name,
        Slug = r.Slug,
        ConnectorSlug = r.ConnectorSlug,
        Method = r.Method,
        PathTemplate = r.PathTemplate,
        HeaderTemplates = r.HeaderTemplates,
        BodyTemplate = r.BodyTemplate,
        BodyContentType = r.BodyContentType,
        QuerySlug = r.QuerySlug,
        Success = r.Success.ToString(),
        SuccessJsonPath = r.SuccessJsonPath,
        CreatedAt = r.CreatedAt,
        UpdatedAt = r.UpdatedAt,
    };
}

internal class SaveRequestRequest
{
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string ConnectorSlug { get; set; } = string.Empty;
    public string Method { get; set; } = "POST";
    public string PathTemplate { get; set; } = string.Empty;
    public Dictionary<string, string> HeaderTemplates { get; set; } = new();
    public string? BodyTemplate { get; set; }
    public string BodyContentType { get; set; } = "application/json";
    public string? QuerySlug { get; set; }
    public string Success { get; set; } = nameof(SuccessRule.TwoHundredRange);
    public string? SuccessJsonPath { get; set; }
}

/// <summary>What would be sent, without sending it.</summary>
internal sealed class DryRunResponse
{
    public bool WouldSend { get; init; }
    public string? Refusal { get; init; }
    public string Method { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public Dictionary<string, string> Headers { get; init; } = new();
    public string? Body { get; init; }
}

internal static class RequestRules
{
    internal static readonly string[] Roles = ["SuperAdmin", "Admin"];

    /// <summary>Methods a configured integration may use.</summary>
    /// <remarks>
    /// An allowlist rather than anything the caller types. `TRACE` against some proxies echoes
    /// request headers, including the Authorization header this attaches, which is a way to read a
    /// credential back out of a connector that never returns one.
    /// </remarks>
    internal static readonly string[] Methods = ["GET", "POST", "PUT", "PATCH", "DELETE"];

    internal static bool IsSlug(string value) =>
        System.Text.RegularExpressions.Regex.IsMatch(value, "^[a-z0-9][a-z0-9-]{0,62}$");

    internal static string? Check(SaveRequestRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name)) return "Name is required.";
        if (!IsSlug(req.Slug)) return "Slug must be lowercase letters, digits and hyphens.";
        if (!IsSlug(req.ConnectorSlug)) return "ConnectorSlug must name a connector.";

        if (!Methods.Contains(req.Method?.ToUpperInvariant()))
        {
            return $"Method must be one of: {string.Join(", ", Methods)}.";
        }

        if (!Enum.TryParse<SuccessRule>(req.Success, ignoreCase: true, out var rule))
        {
            return $"Success must be one of: {string.Join(", ", Enum.GetNames<SuccessRule>())}.";
        }

        if (rule == SuccessRule.TwoHundredAndJsonPathAbsent && string.IsNullOrWhiteSpace(req.SuccessJsonPath))
        {
            // Refused rather than silently behaving like TwoHundredRange. An operator who picked
            // this rule has a provider that lies about status codes, and a rule that quietly does
            // nothing would let it keep lying.
            return "SuccessJsonPath is required when Success is TwoHundredAndJsonPathAbsent.";
        }

        return null;
    }
}

internal sealed class ListRequestsEndpoint : Endpoint<ListRequest, PaginatedResponse<RequestResponse>>
{
    private readonly IQuerySession _session;

    public ListRequestsEndpoint(IQuerySession session) => _session = session;

    public override void Configure()
    {
        Get("/api/requests");
        Roles(RequestRules.Roles);
    }

    public override async Task HandleAsync(ListRequest req, CancellationToken ct)
    {
        var page = await _session.Query<RequestDefinition>().OrderBy(r => r.Name).ToPagedResponseAsync(req, ct);

        await Send.ResponseAsync(new PaginatedResponse<RequestResponse>
        {
            Items = page.Items.Select(RequestResponse.From).ToList(),
            Page = page.Page,
            PageSize = page.PageSize,
            TotalItems = page.TotalItems,
        }, cancellation: ct);
    }
}

internal sealed class GetRequestEndpoint : EndpointWithoutRequest<RequestResponse>
{
    private readonly IQuerySession _session;

    public GetRequestEndpoint(IQuerySession session) => _session = session;

    public override void Configure()
    {
        Get("/api/requests/{slug}");
        Roles(RequestRules.Roles);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var slug = Route<string>("slug") ?? string.Empty;

        if (!RequestRules.IsSlug(slug))
        {
            ThrowError("That is not a request slug: lowercase letters, digits and hyphens only.", 400);
            return;
        }

        var found = await _session.Query<RequestDefinition>().FirstOrDefaultAsync(r => r.Slug == slug, ct);
        if (found is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.ResponseAsync(RequestResponse.From(found), cancellation: ct);
    }
}

internal sealed class SaveRequestEndpoint : Endpoint<SaveRequestRequest, RequestResponse>
{
    private readonly IDocumentSession _session;
    private readonly barakoCMS.Infrastructure.Multitenancy.TenantContext _tenant;

    public SaveRequestEndpoint(IDocumentSession session, barakoCMS.Infrastructure.Multitenancy.TenantContext tenant)
    {
        _session = session;
        _tenant = tenant;
    }

    public override void Configure()
    {
        Post("/api/requests");
        Roles(RequestRules.Roles);
    }

    public override async Task HandleAsync(SaveRequestRequest req, CancellationToken ct)
    {
        var problem = RequestRules.Check(req);
        if (problem is not null)
        {
            ThrowError(problem, 400);
            return;
        }

        // The connector has to exist. A request naming one that does not is a workflow that fails at
        // run time with a message about something the operator cannot see from this screen.
        var connector = await _session.Query<Connector>()
            .FirstOrDefaultAsync(c => c.Slug == req.ConnectorSlug, ct);

        if (connector is null)
        {
            ThrowError($"No connector with the slug '{req.ConnectorSlug}'.", 400);
            return;
        }

        var existing = await _session.Query<RequestDefinition>().FirstOrDefaultAsync(r => r.Slug == req.Slug, ct);
        var definition = existing ?? new RequestDefinition { Id = Guid.NewGuid(), Slug = req.Slug.ToLowerInvariant() };

        definition.Name = req.Name.Trim();
        definition.ConnectorSlug = req.ConnectorSlug.ToLowerInvariant();
        definition.Method = req.Method.ToUpperInvariant();
        definition.PathTemplate = req.PathTemplate;
        definition.HeaderTemplates = req.HeaderTemplates;
        definition.BodyTemplate = req.BodyTemplate;
        definition.BodyContentType = req.BodyContentType;
        definition.QuerySlug = req.QuerySlug;
        definition.Success = Enum.Parse<SuccessRule>(req.Success, ignoreCase: true);
        definition.SuccessJsonPath = req.SuccessJsonPath;
        definition.UpdatedAt = DateTime.UtcNow;

        _session.Store(definition);

        var actorId = Guid.TryParse(User.FindFirst("UserId")?.Value, out var parsed) ? parsed : (Guid?)null;
        await AuditLog.RecordAsync(_session, _tenant.Slug,
            existing is null ? "request.created" : "request.updated",
            actorId, User.FindFirst("Username")?.Value,
            targetType: nameof(RequestDefinition), targetId: definition.Id.ToString(),
            metadata: new Dictionary<string, object>
            {
                ["slug"] = definition.Slug,
                ["connector"] = definition.ConnectorSlug,
                ["method"] = definition.Method,
                // The path, not the body. A body template can carry an operator's own prose and it
                // is not what a reviewer is asking about; where the call goes is.
                ["path"] = definition.PathTemplate,
            }, ct: ct);

        await _session.SaveChangesAsync(ct);

        await Send.ResponseAsync(RequestResponse.From(definition), cancellation: ct);
    }
}

internal sealed class DeleteRequestEndpoint : EndpointWithoutRequest
{
    private readonly IDocumentSession _session;
    private readonly barakoCMS.Infrastructure.Multitenancy.TenantContext _tenant;

    public DeleteRequestEndpoint(IDocumentSession session, barakoCMS.Infrastructure.Multitenancy.TenantContext tenant)
    {
        _session = session;
        _tenant = tenant;
    }

    public override void Configure()
    {
        Delete("/api/requests/{slug}");
        Roles(RequestRules.Roles);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var slug = Route<string>("slug") ?? string.Empty;

        if (!RequestRules.IsSlug(slug))
        {
            ThrowError("That is not a request slug: lowercase letters, digits and hyphens only.", 400);
            return;
        }

        var found = await _session.Query<RequestDefinition>().FirstOrDefaultAsync(r => r.Slug == slug, ct);
        if (found is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        _session.Delete(found);

        var actorId = Guid.TryParse(User.FindFirst("UserId")?.Value, out var parsed) ? parsed : (Guid?)null;
        await AuditLog.RecordAsync(_session, _tenant.Slug, "request.deleted", actorId,
            User.FindFirst("Username")?.Value,
            targetType: nameof(RequestDefinition), targetId: found.Id.ToString(),
            metadata: new Dictionary<string, object> { ["slug"] = found.Slug }, ct: ct);

        await _session.SaveChangesAsync(ct);
        await Send.NoContentAsync(ct);
    }
}

/// <summary>
/// Composes everything and returns the exact call, without making it.
/// </summary>
/// <remarks>
/// The most useful screen in this feature. A template is written against a schema and read by a
/// third party, and the gap between those is where an integration is wrong in ways nobody sees until
/// a customer does. Seeing the finished body before anything is sent closes it.
///
/// No credential appears here. The connector's secrets are attached by the sender, after this, so
/// there is nothing for this endpoint to redact: it never had one.
/// </remarks>
internal sealed class DryRunRequestEndpoint : EndpointWithoutRequest<DryRunResponse>
{
    private readonly IQuerySession _session;
    private readonly IRequestComposer _composer;

    public DryRunRequestEndpoint(IQuerySession session, IRequestComposer composer)
    {
        _session = session;
        _composer = composer;
    }

    public override void Configure()
    {
        Post("/api/requests/{slug}/dry-run/{contentId}");
        Roles(RequestRules.Roles);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var slug = Route<string>("slug") ?? string.Empty;

        if (!RequestRules.IsSlug(slug))
        {
            ThrowError("That is not a request slug: lowercase letters, digits and hyphens only.", 400);
            return;
        }

        if (!Guid.TryParse(Route<string>("contentId"), out var contentId))
        {
            ThrowError("The content id is not a GUID.", 400);
            return;
        }

        var definition = await _session.Query<RequestDefinition>().FirstOrDefaultAsync(r => r.Slug == slug, ct);
        if (definition is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var connector = await _session.Query<Connector>()
            .FirstOrDefaultAsync(c => c.Slug == definition.ConnectorSlug, ct);

        if (connector is null)
        {
            await Send.ResponseAsync(new DryRunResponse
            {
                WouldSend = false,
                Refusal = $"Request '{slug}' names connector '{definition.ConnectorSlug}', which does not exist.",
            }, cancellation: ct);
            return;
        }

        var content = await _session.LoadAsync<barakoCMS.Models.Content>(contentId, ct);
        if (content is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var composed = await _composer.ComposeAsync(definition, connector, content, ct);

        await Send.ResponseAsync(new DryRunResponse
        {
            WouldSend = composed.Ok,
            Refusal = composed.Refusal,
            Method = composed.Method,
            Url = composed.Url,
            Headers = composed.Headers,
            Body = composed.Body,
        }, cancellation: ct);
    }
}
