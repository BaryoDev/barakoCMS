using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Audit;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Auth.Stations;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using FastEndpoints;
using Marten;
using Marten.Patching;

namespace barakoCMS.Features.Stations;

internal sealed class CreateStationRequest
{
    public string Name { get; set; } = string.Empty;
    public List<string> Capabilities { get; set; } = [];
}

internal sealed class CreatedStation
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>Shown once. Only its hash is kept.</summary>
    public string Key { get; set; } = string.Empty;

    public string DisplayPrefix { get; set; } = string.Empty;
    public List<string> Capabilities { get; set; } = [];
}

internal sealed class StationSummary
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string DisplayPrefix { get; set; } = string.Empty;
    public List<string> Capabilities { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public bool Revoked { get; set; }
}

internal sealed class RevokeStationRequest
{
    public Guid Id { get; set; }
}

/// <summary>
/// Creates a station key in the caller's tenant. The caller may only hand out capabilities they
/// hold themselves, so managing stations is not a way to gain access.
/// </summary>
[barakoCMS.Infrastructure.Filters.NoIdempotentReplay]
internal sealed class CreateStationEndpoint(
    IDocumentSession session,
    TenantContext tenant,
    CapabilityVocabulary vocabulary,
    ICallerCapabilities callers) : Endpoint<CreateStationRequest, CreatedStation>
{
    public override void Configure()
    {
        Post("/api/stations");
        Definition.RequireCapability(SystemCapabilities.ManageStations, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(CreateStationRequest req, CancellationToken ct)
    {
        var name = (req.Name ?? string.Empty).Trim();
        var capabilities = (req.Capabilities ?? [])
            .Select(c => (c ?? string.Empty).Trim()).Where(c => c.Length > 0).Distinct().ToList();

        if (name.Length is 0 or > 100) AddError(r => r.Name, "Name is required, up to 100 characters.");
        if (capabilities.Count == 0) AddError(r => r.Capabilities, "Grant at least one capability.");
        if (capabilities.Contains("*")) AddError(r => r.Capabilities, "A station cannot hold every capability. Name each one.");
        ThrowIfAnyErrors();

        foreach (var unknown in vocabulary.Unknown(capabilities))
            AddError(r => r.Capabilities, CapabilityVocabulary.UnknownMessage(unknown));
        ThrowIfAnyErrors();

        foreach (var capability in capabilities)
        {
            if (!await callers.HoldsAsync(User, capability, ct))
                AddError(r => r.Capabilities, $"You cannot grant '{capability}' because you do not hold it.");
        }
        ThrowIfAnyErrors();

        if (!Guid.TryParse(User.FindFirst("UserId")?.Value, out var createdBy))
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var (secret, displayPrefix, hash) = StationKeys.Generate();
        var station = new Station
        {
            Id = Guid.NewGuid(),
            Name = name,
            TenantSlug = tenant.Slug,
            KeyHash = hash,
            DisplayPrefix = displayPrefix,
            Capabilities = capabilities,
            CreatedBy = createdBy,
        };
        session.Store(station);
        await AuditLog.RecordAsync(session, tenant.Slug, "station.created", createdBy,
            User.FindFirst("Username")?.Value,
            targetType: "Station", targetId: station.Id.ToString(),
            metadata: new() { ["capabilities"] = string.Join(",", capabilities) }, ct: ct);
        await session.SaveChangesAsync(ct);

        await Send.OkAsync(new CreatedStation
        {
            Id = station.Id,
            Name = station.Name,
            Key = secret,
            DisplayPrefix = displayPrefix,
            Capabilities = capabilities,
        }, ct);
    }
}

internal sealed class ListStationsEndpoint(IQuerySession session, TenantContext tenant) : EndpointWithoutRequest<List<StationSummary>>
{
    public override void Configure()
    {
        Get("/api/stations");
        Definition.RequireCapability(SystemCapabilities.ManageStations, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var slug = tenant.Slug;
        var stations = await session.Query<Station>()
            .Where(s => s.TenantSlug == slug)
            .OrderBy(s => s.Name)
            .ToListAsync(ct);

        await Send.OkAsync(stations.Select(s => new StationSummary
        {
            Id = s.Id,
            Name = s.Name,
            DisplayPrefix = s.DisplayPrefix,
            Capabilities = s.Capabilities,
            CreatedAt = new DateTimeOffset(DateTime.SpecifyKind(s.CreatedAt, DateTimeKind.Utc)),
            Revoked = s.Revoked,
        }).ToList(), ct);
    }
}

/// <summary>Takes effect on the station's next request: the key is checked against the store every time.</summary>
internal sealed class RevokeStationEndpoint(IDocumentSession session, TenantContext tenant) : Endpoint<RevokeStationRequest>
{
    public override void Configure()
    {
        Post("/api/stations/{id}/revoke");
        Definition.RequireCapability(SystemCapabilities.ManageStations, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(RevokeStationRequest req, CancellationToken ct)
    {
        var station = await session.LoadAsync<Station>(req.Id, ct);
        if (station is null || station.TenantSlug != tenant.Slug)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // A field patch, so a concurrent write to another field cannot undo the revoke.
        session.Patch<Station>(station.Id).Set(x => x.Revoked, true);
        session.Patch<Station>(station.Id).Set(x => x.RevokedAt, (DateTime?)DateTime.UtcNow);
        await AuditLog.RecordAsync(session, tenant.Slug, "station.revoked",
            Guid.TryParse(User.FindFirst("UserId")?.Value, out var by) ? by : null,
            User.FindFirst("Username")?.Value,
            targetType: "Station", targetId: station.Id.ToString(), ct: ct);
        await session.SaveChangesAsync(ct);

        await Send.NoContentAsync(ct);
    }
}
