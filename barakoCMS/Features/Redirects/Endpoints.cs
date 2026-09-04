using barakoCMS.Infrastructure.Audit;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace barakoCMS.Features.Redirects;

/// <summary>
/// The role names that gated redirects before <see cref="SystemCapabilities.ManageRedirects"/>,
/// kept as the legacy fallback so an upgrade does not lock a deployment out.
/// </summary>
/// <remarks>
/// The same pair the other editorial surfaces use. A redirect is content decisions rather than
/// infrastructure, which is the whole argument for it living here, so it is gated like content.
/// </remarks>
internal static class RedirectGate
{
    public static readonly string[] LegacyRoles = ["SuperAdmin", "Admin"];
}

internal sealed class RedirectResponse
{
    public Guid Id { get; init; }
    public string FromPath { get; init; } = string.Empty;
    public string ToPath { get; init; } = string.Empty;
    public bool Permanent { get; init; }
    public string? Note { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }

    public static RedirectResponse From(UrlRedirect r) => new()
    {
        Id = r.Id,
        FromPath = r.FromPath,
        ToPath = r.ToPath,
        Permanent = r.Permanent,
        Note = r.Note,
        CreatedAt = r.CreatedAt,
        UpdatedAt = r.UpdatedAt,
    };
}

internal sealed class ListRedirectsEndpoint : Endpoint<PaginatedRequest, PaginatedResponse<RedirectResponse>>
{
    private readonly IQuerySession _session;

    public ListRedirectsEndpoint(IQuerySession session) => _session = session;

    public override void Configure()
    {
        Get("/api/redirects");
        Definition.RequireCapability(SystemCapabilities.ManageRedirects, RedirectGate.LegacyRoles);
    }

    public override async Task HandleAsync(PaginatedRequest req, CancellationToken ct)
    {
        var all = await _session.Query<UrlRedirect>().OrderBy(r => r.FromPath).ToListAsync(ct);

        await Send.OkAsync(new PaginatedResponse<RedirectResponse>
        {
            Items = all.Skip(req.Skip).Take(req.Take).Select(RedirectResponse.From).ToList(),
            Page = req.Page,
            PageSize = req.PageSize,
            TotalItems = all.Count,
        }, ct);
    }
}

internal sealed class SaveRedirectRequest
{
    public Guid? Id { get; set; }
    public string FromPath { get; set; } = string.Empty;
    public string ToPath { get; set; } = string.Empty;
    public bool Permanent { get; set; }
    public string? Note { get; set; }
}

internal sealed class SaveRedirectEndpoint : Endpoint<SaveRedirectRequest, RedirectResponse>
{
    private readonly IDocumentSession _session;
    private readonly barakoCMS.Infrastructure.Multitenancy.TenantContext _tenant;

    public SaveRedirectEndpoint(
        IDocumentSession session, barakoCMS.Infrastructure.Multitenancy.TenantContext tenant)
    {
        _session = session;
        _tenant = tenant;
    }

    public override void Configure()
    {
        Post("/api/redirects");
        Definition.RequireCapability(SystemCapabilities.ManageRedirects, RedirectGate.LegacyRoles);
    }

    public override async Task HandleAsync(SaveRedirectRequest req, CancellationToken ct)
    {
        var from = UrlRedirect.Normalize(req.FromPath);
        var to = UrlRedirect.Normalize(req.ToPath);

        var stored = await _session.Query<UrlRedirect>().ToListAsync(ct);

        // The rule being edited is left out of the map it is checked against, or every edit reads as
        // a loop with itself.
        var existing = stored
            .Where(r => r.Id != req.Id)
            .ToDictionary(r => r.FromPath, r => r.ToPath, StringComparer.Ordinal);

        if (RedirectRules.Refuse(from, to, existing) is { } refusal)
        {
            AddError(refusal);
            await Send.ErrorsAsync(400, ct);
            return;
        }

        var clash = stored.FirstOrDefault(r => r.Id != req.Id && r.FromPath == from);
        if (clash is not null)
        {
            AddError($"'{from}' already redirects to '{clash.ToPath}'. Edit that rule instead.");
            await Send.ErrorsAsync(409, ct);
            return;
        }

        var redirect = req.Id is { } id ? stored.FirstOrDefault(r => r.Id == id) : null;

        if (req.Id is not null && redirect is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        redirect ??= new UrlRedirect { Id = Guid.NewGuid() };

        redirect.FromPath = from;
        redirect.ToPath = to;
        redirect.Permanent = req.Permanent;
        redirect.Note = req.Note;
        redirect.UpdatedAt = DateTime.UtcNow;

        _session.Store(redirect);

        var actorId = Guid.TryParse(User.FindFirst("UserId")?.Value, out var parsed) ? parsed : (Guid?)null;
        await AuditLog.RecordAsync(_session, _tenant.Slug, "redirect.saved", actorId,
            User.FindFirst("Username")?.Value,
            targetType: nameof(UrlRedirect), targetId: redirect.Id.ToString(),
            metadata: new Dictionary<string, object>
            {
                ["from"] = from,
                ["to"] = to,
                ["permanent"] = req.Permanent,
            }, ct: ct);

        await _session.SaveChangesAsync(ct);

        await Send.OkAsync(RedirectResponse.From(redirect), ct);
    }
}

internal sealed class DeleteRedirectRequest
{
    public Guid Id { get; set; }
}

internal sealed class DeleteRedirectEndpoint : Endpoint<DeleteRedirectRequest>
{
    private readonly IDocumentSession _session;
    private readonly barakoCMS.Infrastructure.Multitenancy.TenantContext _tenant;

    public DeleteRedirectEndpoint(
        IDocumentSession session, barakoCMS.Infrastructure.Multitenancy.TenantContext tenant)
    {
        _session = session;
        _tenant = tenant;
    }

    public override void Configure()
    {
        Delete("/api/redirects/{id}");
        Definition.RequireCapability(SystemCapabilities.ManageRedirects, RedirectGate.LegacyRoles);
    }

    public override async Task HandleAsync(DeleteRedirectRequest req, CancellationToken ct)
    {
        var redirect = await _session.LoadAsync<UrlRedirect>(req.Id, ct);
        if (redirect is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        _session.Delete(redirect);

        var actorId = Guid.TryParse(User.FindFirst("UserId")?.Value, out var parsed) ? parsed : (Guid?)null;
        await AuditLog.RecordAsync(_session, _tenant.Slug, "redirect.deleted", actorId,
            User.FindFirst("Username")?.Value,
            targetType: nameof(UrlRedirect), targetId: redirect.Id.ToString(),
            metadata: new Dictionary<string, object> { ["from"] = redirect.FromPath }, ct: ct);

        await _session.SaveChangesAsync(ct);

        await Send.NoContentAsync(ct);
    }
}
