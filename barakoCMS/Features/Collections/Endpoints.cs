using barakoCMS.Infrastructure.Audit;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Infrastructure.Sync;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace barakoCMS.Features.Collections;

/// <summary>
/// Shared plumbing for the collection sync slices: the gate and how a change is recorded.
/// </summary>
/// <remarks>
/// A new surface, so there is no legacy role pair to preserve the way the connector routes have one.
/// The capability is the only way in, and a deployment upgrading gets it through the seeder's
/// capability backfill.
///
/// Every write is audited. A sync says which third party this instance calls, how often, and which
/// of its values become published content, so "who pointed this collection at that URL" is a
/// question the audit log has to be able to answer.
/// </remarks>
internal static class CollectionSyncGate
{
    internal static Task AuditAsync(
        IDocumentSession session,
        string tenantSlug,
        string action,
        CollectionSync sync,
        System.Security.Claims.ClaimsPrincipal user,
        CancellationToken ct = default)
    {
        var actorId = Guid.TryParse(user.FindFirst("UserId")?.Value, out var parsed) ? parsed : (Guid?)null;

        var metadata = new Dictionary<string, object>
        {
            ["slug"] = sync.Slug,
            ["contentType"] = sync.ContentType,
            ["source"] = sync.Source.ToString(),
            ["from"] = sync.Source == SyncSource.Feed ? sync.FeedUrl ?? string.Empty : sync.RequestSlug ?? string.Empty,
            ["intervalMinutes"] = sync.IntervalMinutes,
        };

        return AuditLog.RecordAsync(session, tenantSlug, action, actorId, user.FindFirst("Username")?.Value,
            targetType: nameof(CollectionSync), targetId: sync.Id.ToString(), metadata: metadata, ct: ct);
    }
}

/// <summary>The checks that need the database, so cannot live in the FluentValidation validator.</summary>
internal static class CollectionSyncRules
{
    /// <summary>The shape a slug has to have, checked on the route value as well as on a save.</summary>
    /// <remarks>
    /// Applied to the route value too. A slug that cannot exist is a malformed request rather than a
    /// missing sync, and answering 404 to it says "no such sync" when the truthful answer is "that is
    /// not a sync name".
    /// </remarks>
    internal static bool IsSlug(string value) =>
        System.Text.RegularExpressions.Regex.IsMatch(value, "^[a-z0-9][a-z0-9-]{0,62}$");

    /// <summary>Returns why this sync cannot be saved, or null.</summary>
    internal static async Task<string?> CheckAsync(
        IQuerySession session, SaveCollectionSyncRequest req, CancellationToken ct)
    {
        if (!Enum.TryParse<SyncSource>(req.Source, ignoreCase: true, out _)
            || !Enum.TryParse<ContentStatus>(req.EntryStatus, ignoreCase: true, out _))
        {
            // Same reason as the floor lookup below. Apply parses both with Enum.Parse, so a
            // request that reached here without the validator would throw rather than be refused.
            return "Source and EntryStatus each have to name one of the values the API documents.";
        }

        var schema = await session.Query<ContentTypeDefinition>()
            .FirstOrDefaultAsync(d => d.Name == req.ContentType, ct);

        if (schema is null)
        {
            return $"There is no content type called '{req.ContentType}' to fill.";
        }

        foreach (var field in req.FieldMap.Keys)
        {
            var definition = schema.Fields.FirstOrDefault(
                f => string.Equals(f.Name, field, StringComparison.OrdinalIgnoreCase));

            if (definition is null)
            {
                return $"'{req.ContentType}' has no field called '{field}'.";
            }

            // A field that is not Public would be written from outside and then masked on the way
            // out, so the sync would appear to work and the page would be empty. Refused for the
            // same reason the request composer refuses to send one: silently doing half the thing is
            // worse than not doing it.
            if (definition.Sensitivity != SensitivityLevel.Public)
            {
                return $"'{field}' is {definition.Sensitivity} on '{req.ContentType}', so it cannot be "
                     + "filled from an outside source.";
            }
        }

        foreach (var floor in req.FloorFields)
        {
            // FirstOrDefault, though the validator already refuses a floor that FieldMap does not
            // name and the loop above refuses a mapped field the type does not have. Depending on
            // one check to make another one safe turns a 400 into a 500 the day either moves.
            var definition = schema.Fields.FirstOrDefault(
                f => string.Equals(f.Name, floor, StringComparison.OrdinalIgnoreCase));

            if (definition is null)
            {
                return $"'{req.ContentType}' has no field called '{floor}' to put a floor on.";
            }

            if (!IsNumeric(definition.Type))
            {
                return $"'{floor}' is a {definition.Type} field, and a floor keeps the greater of two "
                     + "numbers, so it only means something on int, decimal or money.";
            }
        }

        if (!string.Equals(req.Source, nameof(SyncSource.Feed), StringComparison.OrdinalIgnoreCase))
        {
            var definition = await session.Query<RequestDefinition>()
                .FirstOrDefaultAsync(r => r.Slug == req.RequestSlug, ct);

            if (definition is null)
            {
                return $"There is no request definition with the slug '{req.RequestSlug}'.";
            }

            if (!await session.Query<Connector>().AnyAsync(c => c.Slug == definition.ConnectorSlug, ct))
            {
                return $"Request '{definition.Slug}' names connector '{definition.ConnectorSlug}', "
                     + "which does not exist.";
            }
        }

        return null;
    }

    private static bool IsNumeric(string fieldType) =>
        fieldType.ToLowerInvariant() is "int" or "decimal" or "money";

    /// <summary>Copies a validated request onto the document.</summary>
    internal static void Apply(CollectionSync sync, SaveCollectionSyncRequest req)
    {
        sync.Name = req.Name.Trim();
        sync.ContentType = req.ContentType.Trim();
        sync.Enabled = req.Enabled;
        sync.Source = Enum.Parse<SyncSource>(req.Source, ignoreCase: true);
        sync.RequestSlug = sync.Source == SyncSource.Feed ? null : req.RequestSlug?.Trim();
        sync.FeedUrl = sync.Source == SyncSource.Feed ? req.FeedUrl?.Trim() : null;
        sync.ItemsPath = req.ItemsPath.Trim();
        sync.FieldMap = req.FieldMap;
        sync.KeyField = req.KeyField.Trim();
        sync.FloorFields = req.FloorFields;
        sync.IntervalMinutes = req.IntervalMinutes;
        sync.MaxEntries = req.MaxEntries;
        sync.EntryStatus = Enum.Parse<ContentStatus>(req.EntryStatus, ignoreCase: true);
        sync.UpdatedAt = DateTime.UtcNow;
    }
}

internal sealed class ListCollectionSyncsEndpoint : Endpoint<ListRequest, PaginatedResponse<CollectionSyncResponse>>
{
    private readonly IQuerySession _session;

    public ListCollectionSyncsEndpoint(IQuerySession session) => _session = session;

    public override void Configure()
    {
        Get("/api/collection-syncs");
        Definition.RequireCapability(SystemCapabilities.ManageCollectionSyncs);
    }

    public override async Task HandleAsync(ListRequest req, CancellationToken ct)
    {
        var page = await _session.Query<CollectionSync>().OrderBy(s => s.Name).ToPagedResponseAsync(req, ct);

        await Send.ResponseAsync(new PaginatedResponse<CollectionSyncResponse>
        {
            Items = page.Items.Select(CollectionSyncResponse.From).ToList(),
            Page = page.Page,
            PageSize = page.PageSize,
            TotalItems = page.TotalItems,
        }, cancellation: ct);
    }
}

internal sealed class GetCollectionSyncEndpoint : EndpointWithoutRequest<CollectionSyncResponse>
{
    private readonly IQuerySession _session;

    public GetCollectionSyncEndpoint(IQuerySession session) => _session = session;

    public override void Configure()
    {
        Get("/api/collection-syncs/{slug}");
        Definition.RequireCapability(SystemCapabilities.ManageCollectionSyncs);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var slug = Route<string>("slug") ?? string.Empty;

        if (!CollectionSyncRules.IsSlug(slug))
        {
            ThrowError("That is not a collection sync slug: lowercase letters, digits and hyphens only.", 400);
            return;
        }

        var sync = await _session.Query<CollectionSync>().FirstOrDefaultAsync(s => s.Slug == slug, ct);

        if (sync is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.ResponseAsync(CollectionSyncResponse.From(sync), cancellation: ct);
    }
}

internal sealed class CreateCollectionSyncEndpoint : Endpoint<SaveCollectionSyncRequest, CollectionSyncResponse>
{
    private readonly IDocumentSession _session;
    private readonly TenantContext _tenant;

    public CreateCollectionSyncEndpoint(IDocumentSession session, TenantContext tenant)
    {
        _session = session;
        _tenant = tenant;
    }

    public override void Configure()
    {
        Post("/api/collection-syncs");
        Definition.RequireCapability(SystemCapabilities.ManageCollectionSyncs);
    }

    public override async Task HandleAsync(SaveCollectionSyncRequest req, CancellationToken ct)
    {
        var problem = await CollectionSyncRules.CheckAsync(_session, req, ct);
        if (problem is not null)
        {
            ThrowError(problem, 400);
            return;
        }

        if (await _session.Query<CollectionSync>().AnyAsync(s => s.Slug == req.Slug, ct))
        {
            ThrowError($"A collection sync with the slug '{req.Slug}' already exists.", 409);
            return;
        }

        var sync = new CollectionSync { Id = Guid.NewGuid(), Slug = req.Slug.Trim().ToLowerInvariant() };
        CollectionSyncRules.Apply(sync, req);
        sync.CreatedAt = sync.UpdatedAt;

        _session.Store(sync);

        await CollectionSyncGate.AuditAsync(_session, _tenant.Slug, "collection_sync.created", sync, User, ct);
        await _session.SaveChangesAsync(ct);

        await Send.ResponseAsync(CollectionSyncResponse.From(sync), cancellation: ct);
    }
}

internal sealed class UpdateCollectionSyncEndpoint : Endpoint<SaveCollectionSyncRequest, CollectionSyncResponse>
{
    private readonly IDocumentSession _session;
    private readonly TenantContext _tenant;

    public UpdateCollectionSyncEndpoint(IDocumentSession session, TenantContext tenant)
    {
        _session = session;
        _tenant = tenant;
    }

    public override void Configure()
    {
        Put("/api/collection-syncs/{slug}");
        Definition.RequireCapability(SystemCapabilities.ManageCollectionSyncs);
    }

    public override async Task HandleAsync(SaveCollectionSyncRequest req, CancellationToken ct)
    {
        var slug = Route<string>("slug") ?? string.Empty;

        if (!CollectionSyncRules.IsSlug(slug))
        {
            ThrowError("That is not a collection sync slug: lowercase letters, digits and hyphens only.", 400);
            return;
        }

        var sync = await _session.Query<CollectionSync>().FirstOrDefaultAsync(s => s.Slug == slug, ct);

        if (sync is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // The slug addresses the sync and is not editable here, the same rule the connector routes
        // follow. The body's value is ignored rather than refused, since a form that round-trips the
        // resource sends back what it was given.
        req.Slug = sync.Slug;

        var problem = await CollectionSyncRules.CheckAsync(_session, req, ct);
        if (problem is not null)
        {
            ThrowError(problem, 400);
            return;
        }

        CollectionSyncRules.Apply(sync, req);
        _session.Store(sync);

        await CollectionSyncGate.AuditAsync(_session, _tenant.Slug, "collection_sync.updated", sync, User, ct);
        await _session.SaveChangesAsync(ct);

        await Send.ResponseAsync(CollectionSyncResponse.From(sync), cancellation: ct);
    }
}

internal sealed class DeleteCollectionSyncEndpoint : EndpointWithoutRequest
{
    private readonly IDocumentSession _session;
    private readonly TenantContext _tenant;

    public DeleteCollectionSyncEndpoint(IDocumentSession session, TenantContext tenant)
    {
        _session = session;
        _tenant = tenant;
    }

    public override void Configure()
    {
        Delete("/api/collection-syncs/{slug}");
        Definition.RequireCapability(SystemCapabilities.ManageCollectionSyncs);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var slug = Route<string>("slug") ?? string.Empty;

        if (!CollectionSyncRules.IsSlug(slug))
        {
            ThrowError("That is not a collection sync slug: lowercase letters, digits and hyphens only.", 400);
            return;
        }

        var sync = await _session.Query<CollectionSync>().FirstOrDefaultAsync(s => s.Slug == slug, ct);

        if (sync is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // The entries stay. They are ordinary content: somebody may be linking to them, a block may
        // be rendering them, and deleting a schedule is not a decision to delete a hundred published
        // pages. Removing them is the content erase route, deliberately.
        _session.Delete(sync);

        await CollectionSyncGate.AuditAsync(_session, _tenant.Slug, "collection_sync.deleted", sync, User, ct);
        await _session.SaveChangesAsync(ct);

        await Send.NoContentAsync(ct);
    }
}

/// <summary>
/// Runs one sync now, without waiting for its interval.
/// </summary>
/// <remarks>
/// This is how a mapping is checked while the person who wrote it still has it open, and it is the
/// same code path the sweep takes, so what it reports is what the sweep will do. It answers 200 with
/// the outcome even when the run failed: the request succeeded, and the outcome is the answer.
/// </remarks>
internal sealed class RunCollectionSyncEndpoint : EndpointWithoutRequest<RunCollectionSyncResponse>
{
    private readonly IDocumentSession _session;
    private readonly ICollectionSyncRunner _runner;
    private readonly TenantContext _tenant;

    public RunCollectionSyncEndpoint(IDocumentSession session, ICollectionSyncRunner runner, TenantContext tenant)
    {
        _session = session;
        _runner = runner;
        _tenant = tenant;
    }

    public override void Configure()
    {
        Post("/api/collection-syncs/{slug}/run");
        Definition.RequireCapability(SystemCapabilities.ManageCollectionSyncs);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var slug = Route<string>("slug") ?? string.Empty;

        if (!CollectionSyncRules.IsSlug(slug))
        {
            ThrowError("That is not a collection sync slug: lowercase letters, digits and hyphens only.", 400);
            return;
        }

        var sync = await _session.Query<CollectionSync>().FirstOrDefaultAsync(s => s.Slug == slug, ct);

        if (sync is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await CollectionSyncGate.AuditAsync(_session, _tenant.Slug, "collection_sync.run", sync, User, ct);
        await _session.SaveChangesAsync(ct);

        var outcome = await _runner.RunAsync(sync, ct);

        await Send.ResponseAsync(new RunCollectionSyncResponse
        {
            Succeeded = outcome.Succeeded,
            Created = outcome.Created,
            Updated = outcome.Updated,
            Unchanged = outcome.Unchanged,
            Skipped = outcome.Skipped,
            Error = outcome.Error,
        }, cancellation: ct);
    }
}
