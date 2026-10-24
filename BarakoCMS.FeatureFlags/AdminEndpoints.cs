using System.Security.Claims;
using barakoCMS.Infrastructure.Auth;
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
    public bool IsPublic { get; set; }
    public FlagVisibility Visibility { get; set; }
    public DateTime UpdatedAt { get; set; }

    internal static FlagDto From(FeatureFlag f) => new()
    {
        Key = f.Key,
        Description = f.Description,
        Enabled = f.Enabled,
        TenantSlugs = f.TenantSlugs,
        UserEmails = f.UserEmails,
        RolloutPercent = f.RolloutPercent,
        IsPublic = f.EffectiveVisibility() == FlagVisibility.Public,
        Visibility = f.EffectiveVisibility(),
        UpdatedAt = f.UpdatedAt,
    };
}

/// <summary>
/// Flags are one global table and a flag's targeting names other tenants, so the admin surface is for
/// callers holding manage_feature_flags through a global role, not through a tenant membership.
/// </summary>
internal static class FlagAdmin
{
    public static Task<bool> AllowedAsync(
        IQuerySession session, ClaimsPrincipal user, IConfiguration configuration, CancellationToken ct) =>
        PlatformScope.HoldsGloballyAsync(session, user, configuration,
            FeatureFlagCapabilities.ManageFeatureFlags, FeatureFlagCapabilities.Defaults.LegacyRoles, ct);
}

/// <summary>GET /api/feature-flags/admin — all flags with their full config.</summary>
public class ListFlagsEndpoint(IQuerySession session, IConfiguration configuration) : EndpointWithoutRequest<List<FlagDto>>
{
    public override void Configure()
    {
        Get("/api/feature-flags/admin");
        Definition.RequireCapability(
            FeatureFlagCapabilities.ManageFeatureFlags, FeatureFlagCapabilities.Defaults.LegacyRoles);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!await FlagAdmin.AllowedAsync(session, User, configuration, ct))
        {
            ThrowError(PlatformScope.DeploymentWideMessage, 403);
        }
        var flags = await session.Query<FeatureFlag>().OrderBy(f => f.Key).ToListAsync(ct);
        await Send.ResponseAsync(flags.Select(FlagDto.From).ToList(), cancellation: ct);
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

    /// <summary>
    /// Kept for clients written before levels existed. True means Public. False keeps the flag's
    /// current level (SignedIn if that was Public, SignedIn for a new flag). Visibility wins when both are sent.
    /// </summary>
    public bool? IsPublic { get; set; }

    /// <summary>
    /// Who may see the flag. Left out together with IsPublic, a new flag is Admin only and an
    /// existing flag keeps its level, except that Public drops to SignedIn, since an upsert body
    /// that says nothing about it is not a decision to publish.
    /// </summary>
    public FlagVisibility? Visibility { get; set; }
}

/// <summary>POST /api/feature-flags/admin — create or update a flag (upsert by key).</summary>
public class SaveFlagEndpoint(IDocumentSession session, IConfiguration configuration) : Endpoint<UpsertFlagRequest, FlagDto>
{
    public override void Configure()
    {
        Post("/api/feature-flags/admin");
        Definition.RequireCapability(
            FeatureFlagCapabilities.ManageFeatureFlags, FeatureFlagCapabilities.Defaults.LegacyRoles);
    }

    public override async Task HandleAsync(UpsertFlagRequest req, CancellationToken ct)
    {
        if (!await FlagAdmin.AllowedAsync(session, User, configuration, ct))
        {
            ThrowError(PlatformScope.DeploymentWideMessage, 403);
        }
        var key = (req.Key ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(key)) { AddError("Key is required."); await Send.ErrorsAsync(400, ct); return; }

        var existing = await session.Query<FeatureFlag>().FirstOrDefaultAsync(f => f.Key == key, ct);
        var flag = existing ?? new FeatureFlag { Key = key };
        flag.Description = string.IsNullOrWhiteSpace(req.Description) ? null : req.Description!.Trim();
        flag.Enabled = req.Enabled;
        flag.TenantSlugs = Clean(req.TenantSlugs);
        flag.UserEmails = Clean(req.UserEmails);
        flag.RolloutPercent = Math.Clamp(req.RolloutPercent ?? 100, 0, 100);
        flag.Visibility = ChooseVisibility(req, existing);
        flag.IsPublic = flag.Visibility == FlagVisibility.Public;
        flag.UpdatedAt = DateTime.UtcNow;

        session.Store(flag);
        await session.SaveChangesAsync(ct);
        await Send.ResponseAsync(FlagDto.From(flag), cancellation: ct);
    }

    private static FlagVisibility ChooseVisibility(UpsertFlagRequest req, FeatureFlag? existing)
    {
        if (req.Visibility is { } stated) return stated;
        if (req.IsPublic == true) return FlagVisibility.Public;
        if (existing is null) return req.IsPublic == false ? FlagVisibility.SignedIn : FlagVisibility.Admin;
        var current = existing.EffectiveVisibility();
        return current == FlagVisibility.Public ? FlagVisibility.SignedIn : current;
    }

    private static List<string> Clean(List<string>? xs) =>
        xs?.Select(x => x.Trim().ToLowerInvariant()).Where(x => x.Length > 0).Distinct().ToList() ?? new();
}

public class KeyRequest { public string Key { get; set; } = string.Empty; }

/// <summary>POST /api/feature-flags/admin/{key}/toggle — flip a flag on/off.</summary>
public class ToggleFlagEndpoint(IDocumentSession session, IConfiguration configuration) : Endpoint<KeyRequest, FlagDto>
{
    public override void Configure()
    {
        Post("/api/feature-flags/admin/{key}/toggle");
        Definition.RequireCapability(
            FeatureFlagCapabilities.ManageFeatureFlags, FeatureFlagCapabilities.Defaults.LegacyRoles);
    }

    public override async Task HandleAsync(KeyRequest req, CancellationToken ct)
    {
        if (!await FlagAdmin.AllowedAsync(session, User, configuration, ct))
        {
            ThrowError(PlatformScope.DeploymentWideMessage, 403);
        }
        var key = (req.Key ?? string.Empty).Trim().ToLowerInvariant();
        var flag = await session.Query<FeatureFlag>().FirstOrDefaultAsync(f => f.Key == key, ct);
        if (flag is null) { await Send.NotFoundAsync(ct); return; }
        flag.Enabled = !flag.Enabled;
        flag.UpdatedAt = DateTime.UtcNow;
        session.Store(flag);
        await session.SaveChangesAsync(ct);
        await Send.ResponseAsync(FlagDto.From(flag), cancellation: ct);
    }
}

/// <summary>DELETE /api/feature-flags/admin/{key} — remove a flag.</summary>
public class DeleteFlagEndpoint(IDocumentSession session, IConfiguration configuration) : Endpoint<KeyRequest>
{
    public override void Configure()
    {
        Delete("/api/feature-flags/admin/{key}");
        Definition.RequireCapability(
            FeatureFlagCapabilities.ManageFeatureFlags, FeatureFlagCapabilities.Defaults.LegacyRoles);
    }

    public override async Task HandleAsync(KeyRequest req, CancellationToken ct)
    {
        if (!await FlagAdmin.AllowedAsync(session, User, configuration, ct))
        {
            ThrowError(PlatformScope.DeploymentWideMessage, 403);
        }
        var key = (req.Key ?? string.Empty).Trim().ToLowerInvariant();
        var flag = await session.Query<FeatureFlag>().FirstOrDefaultAsync(f => f.Key == key, ct);
        if (flag is not null) { session.Delete(flag); await session.SaveChangesAsync(ct); }
        await Send.OkAsync(ct);
    }
}
