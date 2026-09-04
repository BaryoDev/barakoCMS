using barakoCMS.Infrastructure.Audit;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace barakoCMS.Features.ContentType.Blueprints.Apply;

internal sealed class Request
{
    public string Name { get; set; } = string.Empty;
}

internal sealed class Response
{
    public string Blueprint { get; init; } = string.Empty;
    public List<CreatedType> Created { get; init; } = new();
}

internal sealed class CreatedType
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public bool IsPubliclyDeliverable { get; init; }
}

/// <summary>
/// POST /api/content-types/blueprints/{name}. Creates every type the blueprint declares, in the
/// caller's tenant.
/// </summary>
/// <remarks>
/// Additive, and all or nothing. A type that already exists is a refusal for the whole blueprint
/// rather than a skip, because a partial apply leaves references pointing at a type whose fields
/// are not the ones the blueprint assumed, and nothing afterwards says which half arrived.
///
/// The types are created the way <c>POST /api/content-types</c> creates one: normalized name,
/// document sourced, sourcing policy recorded against the name. It does not go through the
/// Portability import, which upserts by name and would overwrite the clash this refuses.
/// </remarks>
internal sealed class Endpoint : Endpoint<Request, Response>
{
    private readonly IDocumentSession _session;
    private readonly BlueprintCatalog _catalog;
    private readonly barakoCMS.Core.Interfaces.IContentSourcingPolicy _sourcing;
    private readonly barakoCMS.Infrastructure.OpenApi.DeliveryDocumentCache _openApiCache;
    private readonly barakoCMS.Infrastructure.Multitenancy.TenantContext _tenant;

    public Endpoint(
        IDocumentSession session,
        BlueprintCatalog catalog,
        barakoCMS.Core.Interfaces.IContentSourcingPolicy sourcing,
        barakoCMS.Infrastructure.OpenApi.DeliveryDocumentCache openApiCache,
        barakoCMS.Infrastructure.Multitenancy.TenantContext tenant)
    {
        _session = session;
        _catalog = catalog;
        _sourcing = sourcing;
        _openApiCache = openApiCache;
        _tenant = tenant;
    }

    public override void Configure()
    {
        Post("/api/content-types/blueprints/{name}");
        // Applying a blueprint is creating content types, so it asks for what the create asks for.
        Definition.RequireCapability(SystemCapabilities.ManageContentTypes, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var entry = _catalog.Find(req.Name);
        if (entry is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (!entry.IsValid || entry.Definition is null)
        {
            // The list already said what is wrong. Refused here too, because a blueprint that fails
            // its own validation would be refused type by type on the way in and leave a partial set.
            foreach (var error in entry.Errors)
            {
                AddError(error);
            }

            ThrowIfAnyErrors();
            return;
        }

        var types = BlueprintCatalog.Materialize(entry.Definition);

        var clashes = new List<string>();
        foreach (var type in types)
        {
            // Lowered on both sides for the reason the create endpoint gives: a 3.x import could
            // have stored "Article", and every reader treats that as the same type as "article".
            var name = type.Name;
            var existing = await _session.Query<ContentTypeDefinition>()
                .FirstOrDefaultAsync(x => x.Name.ToLower() == name, ct);
            if (existing is not null)
            {
                clashes.Add(existing.Name);
            }
        }

        if (clashes.Count > 0)
        {
            ThrowError(
                $"Blueprint '{entry.Name}' was not applied: {string.Join(", ", clashes)} "
                + (clashes.Count == 1 ? "already exists" : "already exist")
                + " in this tenant. Applying is additive and never replaces a type.",
                409);
        }

        foreach (var type in types)
        {
            // The decision belongs to the name and outlives the definition. A name decided as event
            // sourced cannot be recreated document sourced, which is what a blueprint type is.
            var standing = await _sourcing.GetAsync(type.Name, ct);
            if (standing is { EventSourced: true })
            {
                ThrowError(
                    $"'{type.Name}' was created before with eventSourced set to true, on "
                    + $"{standing.DecidedAt:yyyy-MM-dd}, and a blueprint type is document sourced. "
                    + "Create that type by hand or choose another blueprint.",
                    409);
            }
        }

        foreach (var type in types)
        {
            _session.Store(type);
            await _sourcing.DecideAsync(type.Name, false, ct);
        }

        var actorId = Guid.TryParse(User.FindFirst("UserId")?.Value, out var parsed) ? parsed : (Guid?)null;
        await AuditLog.RecordAsync(_session, _tenant.Slug, "contenttype.blueprint_applied", actorId,
            User.FindFirst("Username")?.Value,
            targetType: "Blueprint", targetId: entry.Name,
            metadata: new Dictionary<string, object>
            {
                ["created"] = string.Join(", ", types.Select(t => t.Name)),
            }, ct: ct);

        try
        {
            await _session.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (IsUniqueViolation(ex))
        {
            // Two requests read nothing and both inserted; the unique index on the name refused the
            // second. Same answer as the read above rather than the raw Postgres error.
            ThrowError(
                $"Blueprint '{entry.Name}' was not applied: one of its types was created by another "
                + "request at the same time.",
                409);
        }

        if (types.Any(t => t.IsPubliclyDeliverable))
        {
            _openApiCache.Invalidate(_tenant.Slug);
        }

        await Send.OkAsync(new Response
        {
            Blueprint = entry.Name,
            Created = types.Select(t => new CreatedType
            {
                Id = t.Id,
                Name = t.Name,
                DisplayName = t.DisplayName,
                IsPubliclyDeliverable = t.IsPubliclyDeliverable,
            }).ToList(),
        }, ct);
    }

    private static bool IsUniqueViolation(Exception? ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is Npgsql.PostgresException { SqlState: "23505" })
            {
                return true;
            }
        }

        return false;
    }
}
