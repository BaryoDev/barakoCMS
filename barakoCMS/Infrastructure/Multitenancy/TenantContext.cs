namespace barakoCMS.Infrastructure.Multitenancy;

/// <summary>
/// The tenant resolved for the current request (scoped). Defaults to the platform's implicit
/// <see cref="Models.Tenant.DefaultSlug"/> tenant so single-tenant deployments behave unchanged.
/// </summary>
public class TenantContext
{
    public string Slug { get; set; } = Models.Tenant.DefaultSlug;

    public bool IsDefault => Slug == Models.Tenant.DefaultSlug;
}
