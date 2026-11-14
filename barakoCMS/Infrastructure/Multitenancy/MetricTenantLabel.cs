namespace barakoCMS.Infrastructure.Multitenancy;

/// <summary>
/// The <c>tenant</c> label on request, job and workflow run metrics.
/// </summary>
/// <remarks>
/// One series per tenant per other label value, so the count grows with the number of tenants.
/// <c>Metrics:TenantLabel</c> set to <c>false</c> collapses every tenant to <see cref="AllTenants"/>
/// for a deployment with too many to carry. The label is declared either way, so a dashboard query
/// does not change shape when the setting does.
///
/// Workflow run and job metrics take the slug from a stored tenant id. The HTTP label takes it
/// from the request, where in Single mode the slug is whatever the caller sent in <c>X-Tenant</c>
/// or the host. <see cref="ForRequest"/> therefore emits it only when TenantResolutionMiddleware
/// found a registered tenant, and the default tenant otherwise, so a caller cannot mint series.
/// </remarks>
internal static class MetricTenantLabel
{
    public const string ConfigurationKey = "Metrics:TenantLabel";

    /// <summary>The label name every metric in this set uses.</summary>
    public const string Name = "tenant";

    /// <summary>The value when the label is switched off.</summary>
    public const string AllTenants = "all";

    /// <summary>On unless <see cref="ConfigurationKey"/> says <c>false</c>.</summary>
    public static bool Enabled { get; private set; } = true;

    public static void Configure(IConfiguration configuration) =>
        Enabled = configuration.GetValue(ConfigurationKey, true);

    /// <summary>The <c>HttpContext.Items</c> key TenantResolutionMiddleware sets for a registered tenant.</summary>
    public const string RegisteredItem = "barako.tenant.registered";

    /// <summary>The HTTP label value: the request's slug if it is a registered tenant, else the default.</summary>
    public static string ForRequest(Microsoft.AspNetCore.Http.HttpContext context)
    {
        var registered = context.Items.ContainsKey(RegisteredItem);
        var slug = registered ? context.RequestServices.GetService<TenantContext>()?.Slug : null;
        return For(slug);
    }

    /// <summary>The label value for a Marten tenant id or a slug. Null is the default tenant.</summary>
    public static string For(string? tenant) => For(tenant, Enabled);

    internal static string For(string? tenant, bool enabled) =>
        enabled ? TenantScopes.SlugFor(tenant) : AllTenants;
}
