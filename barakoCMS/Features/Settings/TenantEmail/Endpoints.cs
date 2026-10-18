using barakoCMS.Infrastructure.Audit;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Infrastructure.Security;
using barakoCMS.Models;
using FastEndpoints;
using FluentValidation;
using Marten;

namespace barakoCMS.Features.Settings.TenantEmail;

/// <summary>
/// The caller's own tenant's mail override. There is no field that could hold the API key.
/// </summary>
internal sealed class TenantEmailSettingsResponse
{
    public string Tenant { get; set; } = string.Empty;
    public bool ApiKeySet { get; set; }
    public string FromAddress { get; set; } = string.Empty;
    public DateTime? UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}

internal static class TenantEmailSettingsMap
{
    internal static TenantEmailSettingsResponse ToResponse(string tenant, EmailSettings? row) => new()
    {
        Tenant = tenant,
        ApiKeySet = !string.IsNullOrEmpty(row?.ProtectedApiKey),
        FromAddress = row?.FromAddress ?? string.Empty,
        UpdatedAt = row?.UpdatedAt,
        UpdatedBy = row?.UpdatedBy,
    };

    internal const string DefaultTenantMessage =
        "The default tenant has no override. Its mail settings are the host default, at /api/settings/email.";
}

internal sealed class GetTenantEmailSettingsEndpoint(IQuerySession session, TenantContext tenant)
    : EndpointWithoutRequest<TenantEmailSettingsResponse>
{
    public override void Configure()
    {
        Get("/api/tenant-settings/email");
        Definition.RequireCapability(SystemCapabilities.ManageSettings, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (tenant.IsDefault) ThrowError(TenantEmailSettingsMap.DefaultTenantMessage, 400);

        var row = await session.LoadAsync<EmailSettings>(EmailSettings.IdForTenant(tenant.Slug), ct);
        await Send.ResponseAsync(TenantEmailSettingsMap.ToResponse(tenant.Slug, row), cancellation: ct);
    }
}

internal sealed class UpdateTenantEmailSettingsRequest
{
    /// <summary>Null leaves the stored key alone; empty clears it, so the host default applies again.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Null leaves it alone; empty clears it, so the host default applies again.</summary>
    public string? FromAddress { get; set; }
}

internal sealed class UpdateTenantEmailSettingsValidator : Validator<UpdateTenantEmailSettingsRequest>
{
    public UpdateTenantEmailSettingsValidator()
    {
        RuleFor(x => x.FromAddress).MaximumLength(320);
        RuleFor(x => x.ApiKey).MaximumLength(2048);
    }
}

internal sealed class UpdateTenantEmailSettingsEndpoint(
    IDocumentSession session,
    ISecretProtector protector,
    TenantContext tenant) : Endpoint<UpdateTenantEmailSettingsRequest, TenantEmailSettingsResponse>
{
    public override void Configure()
    {
        Put("/api/tenant-settings/email");
        Definition.RequireCapability(SystemCapabilities.ManageSettings, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(UpdateTenantEmailSettingsRequest req, CancellationToken ct)
    {
        if (tenant.IsDefault) ThrowError(TenantEmailSettingsMap.DefaultTenantMessage, 400);

        var id = EmailSettings.IdForTenant(tenant.Slug);
        var row = await session.LoadAsync<EmailSettings>(id, ct)
            ?? new EmailSettings { Id = id, TenantSlug = tenant.Slug };

        var changed = new List<string>();

        if (req.ApiKey is not null)
        {
            var trimmed = req.ApiKey.Trim();
            row.ProtectedApiKey = trimmed.Length == 0 ? string.Empty : protector.Protect(trimmed);
            changed.Add(trimmed.Length == 0 ? "apiKey.cleared" : "apiKey.set");
        }

        if (req.FromAddress is not null)
        {
            var trimmed = req.FromAddress.Trim();
            if (!string.Equals(row.FromAddress, trimmed, StringComparison.Ordinal))
            {
                row.FromAddress = trimmed;
                changed.Add("fromAddress");
            }
        }

        if (changed.Count > 0)
        {
            row.UpdatedAt = DateTime.UtcNow;
            row.UpdatedBy = User.FindFirst("Username")?.Value ?? User.Identity?.Name;
            session.Store(row);

            Guid? actorId = Guid.TryParse(User.FindFirst("UserId")?.Value, out var uid) ? uid : null;
            await AuditLog.RecordAsync(session, tenant.Slug, "settings.email.tenant.changed",
                actorId, row.UpdatedBy,
                targetType: nameof(EmailSettings), targetId: id.ToString(),
                metadata: new() { ["fields"] = string.Join(", ", changed) },
                ct: ct);

            await session.SaveChangesAsync(ct);
        }

        await Send.ResponseAsync(TenantEmailSettingsMap.ToResponse(tenant.Slug, row), cancellation: ct);
    }
}
