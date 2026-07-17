using FastEndpoints;
using Marten;

namespace BarakoCMS.FeatureFlags;

public class FlagDto
{
    public string Key { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool Enabled { get; set; }
    public List<string> TenantSlugs { get; set; } = new();
    public List<string> UserEmails { get; set; } = new();
    public int RolloutPercent { get; set; } = 100;
    public DateTime UpdatedAt { get; set; }

    internal static FlagDto From(FeatureFlag f) => new()
    {
        Key = f.Key,
        Description = f.Description,
        Enabled = f.Enabled,
        TenantSlugs = f.TenantSlugs,
        UserEmails = f.UserEmails,
        RolloutPercent = f.RolloutPercent,
        UpdatedAt = f.UpdatedAt,
    };
}

/// <summary>GET /api/feature-flags/admin — all flags with their full config.</summary>
public class ListFlagsEndpoint : EndpointWithoutRequest<List<FlagDto>>
{
    private readonly IQuerySession _session;
    public ListFlagsEndpoint(IQuerySession session) => _session = session;

    public override void Configure()
    {
        Get("/api/feature-flags/admin");
        Roles("SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var flags = await _session.Query<FeatureFlag>().OrderBy(f => f.Key).ToListAsync(ct);
        await SendAsync(flags.Select(FlagDto.From).ToList(), cancellation: ct);
    }
}

public class UpsertFlagRequest
{
    public string Key { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool Enabled { get; set; }
    public List<string>? TenantSlugs { get; set; }
    public List<string>? UserEmails { get; set; }
    public int? RolloutPercent { get; set; }
}

/// <summary>POST /api/feature-flags/admin — create or update a flag (upsert by key).</summary>
public class SaveFlagEndpoint : Endpoint<UpsertFlagRequest, FlagDto>
{
    private readonly IDocumentSession _session;
    public SaveFlagEndpoint(IDocumentSession session) => _session = session;

    public override void Configure()
    {
        Post("/api/feature-flags/admin");
        Roles("SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(UpsertFlagRequest req, CancellationToken ct)
    {
        var key = (req.Key ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(key)) { AddError("Key is required."); await SendErrorsAsync(400, ct); return; }

        var flag = await _session.Query<FeatureFlag>().FirstOrDefaultAsync(f => f.Key == key, ct)
                   ?? new FeatureFlag { Key = key };
        flag.Description = string.IsNullOrWhiteSpace(req.Description) ? null : req.Description!.Trim();
        flag.Enabled = req.Enabled;
        flag.TenantSlugs = Clean(req.TenantSlugs);
        flag.UserEmails = Clean(req.UserEmails);
        flag.RolloutPercent = Math.Clamp(req.RolloutPercent ?? 100, 0, 100);
        flag.UpdatedAt = DateTime.UtcNow;

        _session.Store(flag);
        await _session.SaveChangesAsync(ct);
        await SendAsync(FlagDto.From(flag), cancellation: ct);
    }

    private static List<string> Clean(List<string>? xs) =>
        xs?.Select(x => x.Trim().ToLowerInvariant()).Where(x => x.Length > 0).Distinct().ToList() ?? new();
}

public class KeyRequest { public string Key { get; set; } = string.Empty; }

/// <summary>POST /api/feature-flags/admin/{key}/toggle — flip a flag on/off.</summary>
public class ToggleFlagEndpoint : Endpoint<KeyRequest, FlagDto>
{
    private readonly IDocumentSession _session;
    public ToggleFlagEndpoint(IDocumentSession session) => _session = session;

    public override void Configure()
    {
        Post("/api/feature-flags/admin/{key}/toggle");
        Roles("SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(KeyRequest req, CancellationToken ct)
    {
        var key = (req.Key ?? string.Empty).Trim().ToLowerInvariant();
        var flag = await _session.Query<FeatureFlag>().FirstOrDefaultAsync(f => f.Key == key, ct);
        if (flag is null) { await SendNotFoundAsync(ct); return; }
        flag.Enabled = !flag.Enabled;
        flag.UpdatedAt = DateTime.UtcNow;
        _session.Store(flag);
        await _session.SaveChangesAsync(ct);
        await SendAsync(FlagDto.From(flag), cancellation: ct);
    }
}

/// <summary>DELETE /api/feature-flags/admin/{key} — remove a flag.</summary>
public class DeleteFlagEndpoint : Endpoint<KeyRequest>
{
    private readonly IDocumentSession _session;
    public DeleteFlagEndpoint(IDocumentSession session) => _session = session;

    public override void Configure()
    {
        Delete("/api/feature-flags/admin/{key}");
        Roles("SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(KeyRequest req, CancellationToken ct)
    {
        var key = (req.Key ?? string.Empty).Trim().ToLowerInvariant();
        var flag = await _session.Query<FeatureFlag>().FirstOrDefaultAsync(f => f.Key == key, ct);
        if (flag is not null) { _session.Delete(flag); await _session.SaveChangesAsync(ct); }
        await SendOkAsync(ct);
    }
}
