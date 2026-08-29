using JasperFx;
using Microsoft.Extensions.DependencyInjection;

namespace barakoCMS.Infrastructure.Multitenancy;

/// <summary>
/// Scope creation for work that happens outside a request.
/// </summary>
/// <remarks>
/// A plain <c>CreateScope()</c> gets a fresh <see cref="TenantContext"/> sitting on the platform
/// default tenant, and <see cref="TenantSessionFactory"/> then opens every session in that scope
/// against the default partition. Inside the projection daemon or a hosted service that is silently
/// the wrong partition: reads find nothing and writes cross the isolation boundary. Naming the
/// tenant is the only way through here, so the next background scope is a compile error rather than
/// a misroute.
/// </remarks>
public static class TenantScopes
{
    /// <summary>
    /// Opens a scope bound to <paramref name="martenTenantId"/>, so sessions resolved inside it read
    /// and write that tenant's partition.
    /// </summary>
    /// <param name="martenTenantId">
    /// The tenant id Marten reports for the work in hand: <c>IEvent.TenantId</c>,
    /// <c>IDocumentOperations.TenantId</c>. Marten's own default marker maps back to
    /// <see cref="Models.Tenant.DefaultSlug"/>.
    /// </param>
    public static IServiceScope CreateScopeForTenant(this IServiceProvider services, string? martenTenantId)
    {
        var scope = services.CreateScope();
        try
        {
            scope.ServiceProvider.GetRequiredService<TenantContext>().Slug = SlugFor(martenTenantId);
        }
        catch
        {
            scope.Dispose();
            throw;
        }

        return scope;
    }

    /// <summary>
    /// Translates a Marten tenant id into the slug <see cref="TenantContext"/> speaks. Marten calls
    /// the default partition <c>*DEFAULT*</c>; the CMS calls it <c>default</c>, and a session opened
    /// under the wrong one of those reads an empty partition.
    /// </summary>
    public static string SlugFor(string? martenTenantId) =>
        string.IsNullOrWhiteSpace(martenTenantId)
        || string.Equals(martenTenantId, StorageConstants.DefaultTenantId, StringComparison.Ordinal)
            ? Models.Tenant.DefaultSlug
            : martenTenantId;
}
