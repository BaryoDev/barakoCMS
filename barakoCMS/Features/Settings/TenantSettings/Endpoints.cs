using barakoCMS.Infrastructure.Audit;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Infrastructure.Security;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using barakoCMS.Modules;
using FastEndpoints;
using FluentValidation;
using Marten;

namespace barakoCMS.Features.Settings.TenantSettingsSections;

internal sealed class TenantSettingsKeyResponse
{
    public string Key { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public bool Secret { get; set; }
    public bool TenantOverridable { get; set; }

    /// <summary>True when this tenant has stored its own value.</summary>
    public bool Set { get; set; }

    /// <summary>The tenant's own value. Always null for a secret, which is write-only.</summary>
    public string? Value { get; set; }
}

internal sealed class TenantSettingsSectionResponse
{
    public string Tenant { get; set; } = string.Empty;
    public string Section { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<TenantSettingsKeyResponse> Keys { get; set; } = [];
    public DateTime? UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}

internal static class TenantSettingsMap
{
    internal const string DefaultTenantMessage =
        "The default tenant has no override. Its settings are the host default.";

    internal static TenantSettingsSectionResponse ToResponse(string tenant, ModuleSettingsSection section, TenantSettings? row)
    {
        var response = new TenantSettingsSectionResponse
        {
            Tenant = tenant,
            Section = section.Name,
            Description = section.Description,
            UpdatedAt = row?.UpdatedAt,
            UpdatedBy = row?.UpdatedBy,
        };

        foreach (var key in section.Keys)
        {
            var id = TenantSettings.ValueKey(section.Name, key.Key);
            var item = new TenantSettingsKeyResponse
            {
                Key = key.Key,
                Description = key.Description,
                Category = key.Category,
                Secret = key.Secret,
                TenantOverridable = key.TenantOverridable,
            };

            if (key.Secret)
            {
                item.Set = row is not null && row.ProtectedValues.TryGetValue(id, out var sealedValue) && !string.IsNullOrEmpty(sealedValue);
            }
            else if (row is not null && row.Values.TryGetValue(id, out var value) && !string.IsNullOrEmpty(value))
            {
                item.Set = true;
                item.Value = value;
            }

            response.Keys.Add(item);
        }

        return response;
    }
}

internal sealed class GetTenantSettingsRequest
{
    public string Section { get; set; } = string.Empty;
}

internal sealed class GetTenantSettingsEndpoint(IQuerySession session, TenantContext tenant, ModuleCatalogue catalogue)
    : Endpoint<GetTenantSettingsRequest, TenantSettingsSectionResponse>
{
    public override void Configure()
    {
        Get("/api/tenant-settings/{section}");
        Definition.RequireCapability(SystemCapabilities.ManageSettings, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(GetTenantSettingsRequest req, CancellationToken ct)
    {
        if (tenant.IsDefault)
        {
            ThrowError(TenantSettingsMap.DefaultTenantMessage, 400);
        }

        var section = TenantSettingsResolver.FindSection(catalogue, req.Section);
        if (section is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var row = await session.LoadAsync<TenantSettings>(TenantSettings.IdForTenant(tenant.Slug), ct);
        await Send.ResponseAsync(TenantSettingsMap.ToResponse(tenant.Slug, section, row), cancellation: ct);
    }
}

internal static class TenantSettingsValues
{
    /// <summary>Whether <paramref name="value"/> fits the declared kind. Empty fits every kind, because it clears.</summary>
    internal static bool Fits(ModuleSettingsKind kind, string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return true;
        }

        return kind switch
        {
            // The same rules as PUT /api/tenant-settings/email.
            ModuleSettingsKind.Mailbox => trimmed.Length <= 320
                && trimmed.AsSpan().IndexOfAny(['\r', '\n', ',', ';']) < 0
                && System.Net.Mail.MailAddress.TryCreate(trimmed, out _),
            ModuleSettingsKind.Url => Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
                && uri.Scheme == Uri.UriSchemeHttps,
            _ => true,
        };
    }

    internal static string Describe(ModuleSettingsKind kind) => kind switch
    {
        ModuleSettingsKind.Mailbox => "email address",
        ModuleSettingsKind.Url => "https URL",
        _ => "value",
    };
}

internal sealed class UpdateTenantSettingsRequest
{
    public string Section { get; set; } = string.Empty;

    /// <summary>Key to value. A missing key is left alone; an empty value clears the override.</summary>
    public Dictionary<string, string?> Values { get; set; } = [];
}

internal sealed class UpdateTenantSettingsValidator : Validator<UpdateTenantSettingsRequest>
{
    public UpdateTenantSettingsValidator()
    {
        RuleFor(x => x.Values).NotNull();
        RuleForEach(x => x.Values).Must(p => p.Value is null || p.Value.Length <= 2048)
            .WithMessage("A value can be at most 2048 characters.");
    }
}

internal sealed class UpdateTenantSettingsEndpoint(
    IDocumentSession session,
    ISecretProtector protector,
    TenantContext tenant,
    ModuleCatalogue catalogue) : Endpoint<UpdateTenantSettingsRequest, TenantSettingsSectionResponse>
{
    public override void Configure()
    {
        Put("/api/tenant-settings/{section}");
        Definition.RequireCapability(SystemCapabilities.ManageSettings, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(UpdateTenantSettingsRequest req, CancellationToken ct)
    {
        if (tenant.IsDefault)
        {
            ThrowError(TenantSettingsMap.DefaultTenantMessage, 400);
        }

        var section = TenantSettingsResolver.FindSection(catalogue, req.Section);
        if (section is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var keys = ValidateKeys(section, req.Values);

        // The row holds every section of the tenant, so the read and the write share one transaction
        // under a lock on the tenant, and two saves cannot overwrite each other's keys.
        var id = TenantSettings.IdForTenant(tenant.Slug);
        await session.BeginTransactionAsync(ct);
        await session.QueryAsync<int>(
            "select 1 from pg_advisory_xact_lock(hashtextextended(?, 0))", ct, $"barakocms:tenant-settings:{tenant.Slug.ToLowerInvariant()}");

        var row = await session.LoadAsync<TenantSettings>(id, ct)
            ?? new TenantSettings { Id = id, TenantSlug = tenant.Slug };

        var changed = ApplyValues(row, section, keys);

        if (changed.Count > 0)
        {
            await StoreChangesAsync(row, id, section, changed, ct);
        }

        await Send.ResponseAsync(TenantSettingsMap.ToResponse(tenant.Slug, section, row), cancellation: ct);
    }

    private List<(ModuleSettingsKey Key, string? Value)> ValidateKeys(ModuleSettingsSection section, Dictionary<string, string?> values)
    {
        // Check every key before anything is written, so a bad key rejects the whole request.
        var keys = new List<(ModuleSettingsKey Key, string? Value)>();
        foreach (var (name, value) in values)
        {
            var key = TenantSettingsResolver.FindKey(section, name);
            if (key is null)
            {
                ThrowError($"'{name}' is not a setting of section '{section.Name}'.", 400);
            }
            if (!key.TenantOverridable)
            {
                ThrowError($"'{key.Key}' is set for the whole host and a tenant cannot override it.", 400);
            }
            if (value is not null && !TenantSettingsValues.Fits(key.Kind, value))
            {
                ThrowError($"'{key.Key}' is not a valid {TenantSettingsValues.Describe(key.Kind)}.", 400);
            }
            keys.Add((key, value));
        }
        return keys;
    }

    private List<string> ApplyValues(TenantSettings row, ModuleSettingsSection section, List<(ModuleSettingsKey Key, string? Value)> keys)
    {
        var changed = new List<string>();
        foreach (var (key, value) in keys)
        {
            if (value is null)
            {
                continue;
            }
            var id2 = TenantSettings.ValueKey(section.Name, key.Key);
            var trimmed = value.Trim();

            if (key.Secret)
            {
                if (trimmed.Length == 0)
                {
                    if (row.ProtectedValues.Remove(id2))
                    {
                        changed.Add($"{key.Key}.cleared");
                    }
                }
                else
                {
                    row.ProtectedValues[id2] = protector.Protect(trimmed);
                    changed.Add($"{key.Key}.set");
                }
            }
            else if (trimmed.Length == 0)
            {
                if (row.Values.Remove(id2))
                {
                    changed.Add($"{key.Key}.cleared");
                }
            }
            else if (!row.Values.TryGetValue(id2, out var current) || !string.Equals(current, trimmed, StringComparison.Ordinal))
            {
                row.Values[id2] = trimmed;
                changed.Add(key.Key);
            }
        }
        return changed;
    }

    private async Task StoreChangesAsync(TenantSettings row, Guid id, ModuleSettingsSection section, List<string> changed, CancellationToken ct)
    {
        row.UpdatedAt = DateTime.UtcNow;
        row.UpdatedBy = User.FindFirst("Username")?.Value ?? User.Identity?.Name;
        session.Store(row);

        Guid? actorId = Guid.TryParse(User.FindFirst("UserId")?.Value, out var uid) ? uid : null;
        await AuditLog.RecordAsync(session, tenant.Slug, "settings.tenant.changed",
            actorId, row.UpdatedBy,
            targetType: nameof(TenantSettings), targetId: id.ToString(),
            metadata: new() { ["section"] = section.Name, ["fields"] = string.Join(", ", changed) },
            ct: ct);

        await session.SaveChangesAsync(ct);
    }
}
