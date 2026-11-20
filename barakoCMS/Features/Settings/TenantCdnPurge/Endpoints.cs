using barakoCMS.Infrastructure.Audit;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Caching.Purge;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Infrastructure.Security;
using barakoCMS.Models;
using FastEndpoints;
using FluentValidation;
using Marten;

namespace barakoCMS.Features.Settings.TenantCdnPurge;

/// <summary>
/// The caller's own tenant's CDN purge settings. There is no field that could hold the token.
/// </summary>
internal sealed class TenantCdnPurgeResponse
{
    public string Tenant { get; set; } = string.Empty;
    public string Provider { get; set; } = CdnPurgeProvider.None;
    public bool Enabled { get; set; }
    public string TargetId { get; set; } = string.Empty;
    public bool TokenSet { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}

internal static class TenantCdnPurgeMap
{
    internal static TenantCdnPurgeResponse ToResponse(string tenant, CdnPurgeSettings? row) => new()
    {
        Tenant = tenant,
        Provider = row?.Provider ?? CdnPurgeProvider.None,
        Enabled = row?.Enabled ?? false,
        TargetId = row?.TargetId ?? string.Empty,
        TokenSet = !string.IsNullOrEmpty(row?.ProtectedToken),
        UpdatedAt = row?.UpdatedAt,
        UpdatedBy = row?.UpdatedBy,
    };

    internal const string DefaultTenantMessage =
        "The default tenant has no per-tenant row. Its CDN purge settings are the host configuration, under Delivery:CdnPurge.";
}

internal sealed class GetTenantCdnPurgeEndpoint(IQuerySession session, TenantContext tenant)
    : EndpointWithoutRequest<TenantCdnPurgeResponse>
{
    public override void Configure()
    {
        Get("/api/tenant-settings/cdn-purge");
        Definition.RequireCapability(SystemCapabilities.ManageSettings, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (tenant.IsDefault)
        {
            ThrowError(TenantCdnPurgeMap.DefaultTenantMessage, 400);
        }

        var row = await session.LoadAsync<CdnPurgeSettings>(CdnPurgeSettings.IdForTenant(tenant.Slug), ct);
        await Send.ResponseAsync(TenantCdnPurgeMap.ToResponse(tenant.Slug, row), cancellation: ct);
    }
}

internal sealed class UpdateTenantCdnPurgeRequest
{
    /// <summary>Null leaves it alone; empty clears it to <c>none</c>.</summary>
    public string? Provider { get; set; }

    /// <summary>Null leaves it alone.</summary>
    public bool? Enabled { get; set; }

    /// <summary>The zone id or service id. Null leaves it alone; empty clears it.</summary>
    public string? TargetId { get; set; }

    /// <summary>Write only. Null leaves the stored token alone; empty clears it.</summary>
    public string? Token { get; set; }
}

internal sealed class UpdateTenantCdnPurgeValidator : Validator<UpdateTenantCdnPurgeRequest>
{
    public UpdateTenantCdnPurgeValidator()
    {
        RuleFor(x => x.Provider)
            .Must(CdnPurgeProvider.IsKnown)
            .When(x => !string.IsNullOrWhiteSpace(x.Provider))
            .WithMessage("Provider must be one of none, cloudflare or fastly.");
        RuleFor(x => x.TargetId)
            .Must(CdnPurgeResolver.IsValidTargetId)
            .When(x => !string.IsNullOrEmpty(x.TargetId))
            .WithMessage("TargetId must be 1 to 64 letters and digits.");
        RuleFor(x => x.Token)
            .MaximumLength(2048)
            .Must(t => t is null || !t.Any(char.IsControl))
            .WithMessage("Token must not contain line breaks or control characters.");
    }
}

[barakoCMS.Infrastructure.Filters.NoIdempotentReplay]
internal sealed class UpdateTenantCdnPurgeEndpoint(
    IDocumentSession session,
    ISecretProtector protector,
    TenantContext tenant) : Endpoint<UpdateTenantCdnPurgeRequest, TenantCdnPurgeResponse>
{
    public override void Configure()
    {
        Put("/api/tenant-settings/cdn-purge");
        Definition.RequireCapability(SystemCapabilities.ManageSettings, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(UpdateTenantCdnPurgeRequest req, CancellationToken ct)
    {
        if (tenant.IsDefault)
        {
            ThrowError(TenantCdnPurgeMap.DefaultTenantMessage, 400);
        }

        // The read and the write share one transaction under a lock on the tenant, so two saves of
        // different fields cannot overwrite each other.
        var id = CdnPurgeSettings.IdForTenant(tenant.Slug);
        await session.BeginTransactionAsync(ct);
        await session.QueryAsync<int>(
            "select 1 from pg_advisory_xact_lock(hashtextextended(?, 0))", ct, $"barakocms:cdn-purge:{tenant.Slug.ToLowerInvariant()}");

        var row = await session.LoadAsync<CdnPurgeSettings>(id, ct)
            ?? new CdnPurgeSettings { Id = id, TenantSlug = tenant.Slug };

        var changed = Apply(row, req);

        if (row.Enabled && !IsComplete(row))
        {
            ThrowError("Enabled needs a provider, a target id and a token.", 400);
        }

        if (changed.Count > 0)
        {
            row.UpdatedAt = DateTime.UtcNow;
            row.UpdatedBy = User.FindFirst("Username")?.Value ?? User.Identity?.Name;
            session.Store(row);

            Guid? actorId = Guid.TryParse(User.FindFirst("UserId")?.Value, out var uid) ? uid : null;
            await AuditLog.RecordAsync(session, tenant.Slug, "settings.cdn-purge.tenant.changed",
                actorId, row.UpdatedBy,
                targetType: nameof(CdnPurgeSettings), targetId: id.ToString(),
                metadata: new() { ["fields"] = string.Join(", ", changed) },
                ct: ct);

            await session.SaveChangesAsync(ct);
        }

        await Send.ResponseAsync(TenantCdnPurgeMap.ToResponse(tenant.Slug, row), cancellation: ct);
    }

    private static bool IsComplete(CdnPurgeSettings row) =>
        (row.Provider == CdnPurgeProvider.Cloudflare || row.Provider == CdnPurgeProvider.Fastly)
        && row.TargetId.Length > 0
        && row.ProtectedToken.Length > 0;

    private List<string> Apply(CdnPurgeSettings row, UpdateTenantCdnPurgeRequest req)
    {
        var changed = new List<string>();

        if (req.Provider is not null)
        {
            var provider = req.Provider.Trim().Length == 0 ? CdnPurgeProvider.None : req.Provider.Trim().ToLowerInvariant();
            if (row.Provider != provider)
            {
                row.Provider = provider;
                changed.Add("provider");
            }
        }

        if (req.TargetId is not null)
        {
            var target = req.TargetId.Trim();
            if (!string.Equals(row.TargetId, target, StringComparison.Ordinal))
            {
                row.TargetId = target;
                changed.Add("targetId");
            }
        }

        if (req.Token is not null)
        {
            var token = req.Token.Trim();
            row.ProtectedToken = token.Length == 0 ? string.Empty : protector.Protect(token);
            changed.Add(token.Length == 0 ? "token.cleared" : "token.set");
        }

        if (req.Enabled is { } enabled && row.Enabled != enabled)
        {
            row.Enabled = enabled;
            changed.Add("enabled");
        }

        return changed;
    }
}
