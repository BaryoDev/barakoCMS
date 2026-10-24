using barakoCMS.Infrastructure.Audit;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace barakoCMS.Features.Settings.Locales;

/// <summary>The caller's tenant's languages. Empty until an admin sets them.</summary>
internal sealed class GetLocaleSettingsEndpoint(IQuerySession session)
    : EndpointWithoutRequest<LocaleSettingsResponse>
{
    public override void Configure()
    {
        Get("/api/settings/locales");
        Definition.RequireCapability(SystemCapabilities.ManageSettings, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var row = await session.LoadAsync<LocaleSettings>(LocaleSettings.SingletonId, ct);
        await Send.ResponseAsync(LocaleSettingsResponse.From(row), cancellation: ct);
    }
}

/// <summary>Replaces the caller's tenant's languages with the body.</summary>
internal sealed class UpdateLocaleSettingsEndpoint(IDocumentSession session, TenantContext tenant)
    : Endpoint<UpdateLocaleSettingsRequest, LocaleSettingsResponse>
{
    public override void Configure()
    {
        Put("/api/settings/locales");
        Definition.RequireCapability(SystemCapabilities.ManageSettings, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(UpdateLocaleSettingsRequest req, CancellationToken ct)
    {
        var row = await session.LoadAsync<LocaleSettings>(LocaleSettings.SingletonId, ct) ?? new LocaleSettings();

        row.DefaultLocale = req.DefaultLocale;
        row.Locales = [.. req.Locales];
        row.FallbackChains = req.FallbackChains.ToDictionary(p => p.Key, p => new List<string>(p.Value));
        row.UpdatedAt = DateTime.UtcNow;
        row.UpdatedBy = User.FindFirst("Username")?.Value ?? User.Identity?.Name;
        session.Store(row);

        Guid? actorId = Guid.TryParse(User.FindFirst("UserId")?.Value, out var id) ? id : null;
        await AuditLog.RecordAsync(session, tenant.Slug, "settings.locales.changed",
            actorId, row.UpdatedBy,
            targetType: nameof(LocaleSettings), targetId: LocaleSettings.SingletonId.ToString(),
            metadata: new() { ["locales"] = string.Join(", ", row.Locales), ["default"] = row.DefaultLocale },
            ct: ct);

        await session.SaveChangesAsync(ct);
        await Send.ResponseAsync(LocaleSettingsResponse.From(row), cancellation: ct);
    }
}
