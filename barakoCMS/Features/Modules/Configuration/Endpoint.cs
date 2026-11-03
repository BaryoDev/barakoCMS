using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using barakoCMS.Modules;
using FastEndpoints;
using Marten;

namespace barakoCMS.Features.Modules.Configuration;

/// <summary>
/// GET /api/modules/{name}/configuration, the configuration an enabled module runs with and where
/// each value came from. Read only. No secret leaves through it: see
/// <see cref="ModuleConfigurationReader"/> for the order of the checks.
/// </summary>
/// <remarks>
/// A caller who holds <c>view_module_configuration</c> only through a tenant role sees the keys a
/// module marks as tenant scope. Deployment scope keys are left out, not redacted, the same split
/// as the settings routes (<see cref="PlatformScope"/>).
///
/// An unknown module and a disabled one both answer 404 with no body, and the name asked for is
/// never echoed.
/// </remarks>
internal sealed class Endpoint(
    ModuleCatalogue catalogue,
    IQuerySession session,
    IConfiguration configuration,
    ITenantSettings tenantSettings,
    TenantContext tenant) : Endpoint<ModuleConfigurationRequest, ModuleConfigurationResponse>
{
    public override void Configure()
    {
        Get("/api/modules/{name}/configuration");
        Definition.RequireCapability(SystemCapabilities.ViewModuleConfiguration, "SuperAdmin");
        Description(b => b
            .Produces<ModuleConfigurationResponse>(200)
            .Produces(401)
            .Produces(403)
            .Produces(404));
    }

    public override async Task HandleAsync(ModuleConfigurationRequest req, CancellationToken ct)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";

        var module = catalogue.Entries.FirstOrDefault(e =>
            e.Enabled && string.Equals(e.Name, req.Name, StringComparison.OrdinalIgnoreCase));
        if (module is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var deployment = await PlatformScope.HoldsGloballyAsync(
            session, User, configuration, SystemCapabilities.ViewModuleConfiguration, ["SuperAdmin"], ct);

        var response = new ModuleConfigurationReader(configuration).Read(module, deployment);
        await Send.OkAsync(await WithTenantOverridesAsync(module, response, ct), ct);
    }

    /// <summary>
    /// Shows a tenant caller its own stored override in place of the host value, for a tenant scope
    /// key the module also declares as a tenant-overridable setting. A secret setting is never
    /// overlaid, and the default tenant has no override to show.
    /// </summary>
    private async Task<ModuleConfigurationResponse> WithTenantOverridesAsync(
        ModuleCatalogueEntry module, ModuleConfigurationResponse response, CancellationToken ct)
    {
        if (tenant.IsDefault || module.SettingsSections.Count == 0)
        {
            return response;
        }

        var prefix = response.Section + ":";
        var entries = response.Entries.ToList();
        foreach (var section in module.SettingsSections)
        {
            foreach (var key in section.Keys)
            {
                if (key.Secret || !key.TenantOverridable || key.HostDefaultKey is null
                    || !key.HostDefaultKey.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var relative = key.HostDefaultKey[prefix.Length..];
                var at = entries.FindIndex(e => e.Scope == "tenant"
                    && string.Equals(e.Key, relative, StringComparison.OrdinalIgnoreCase));
                var declared = module.ConfigurationView?.Keys.Any(k =>
                    string.Equals(k.Key.Trim(':'), relative, StringComparison.OrdinalIgnoreCase)
                    && k.Scope == ConfigurationScope.Tenant) == true;
                if (at < 0 && !declared)
                {
                    continue;
                }

                var own = await tenantSettings.GetOverrideAsync(tenant.Slug, section.Name, key.Key, ct);
                if (string.IsNullOrEmpty(own))
                {
                    continue;
                }

                var shown = new ModuleConfigurationEntry(relative, own, false, "tenant", ConfigurationSources.Tenant);
                if (at < 0)
                {
                    entries.Add(shown);
                }
                else
                {
                    entries[at] = entries[at] with { Value = own, Redacted = false, Source = ConfigurationSources.Tenant };
                }
            }
        }

        return response with { Entries = entries };
    }
}
