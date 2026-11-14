namespace barakoCMS.Core.Interfaces;

/// <summary>
/// The system capabilities a service principal or a person holds in a tenant, read without a request.
/// A hosted service in a module uses it to get the set it passes to <c>INeedsYouSource</c>, so the set
/// comes from the same role rules the route gates use and is never written by the module.
/// </summary>
/// <remarks>
/// The answer is the same one <see cref="ICallerCapabilities"/> gives for a request by the same caller.
/// A person holding the seeded SuperAdmin role gets <c>*</c>. A principal never gets <c>*</c> or a
/// platform capability, and a disabled principal, one of another tenant, or one of a tenant that is not
/// active holds nothing. A person or principal that does not exist holds nothing. Read only, and
/// the tenant is explicit, so no <c>TenantContext</c> is needed.
/// </remarks>
public interface ICapabilityResolver
{
    /// <summary>The capabilities the service principal holds in <paramref name="tenant"/>.</summary>
    Task<IReadOnlySet<string>> ForPrincipalAsync(Guid principalId, string tenant, CancellationToken cancellationToken = default);

    /// <summary>The capabilities the person holds in <paramref name="tenant"/>, from the roles they hold there.</summary>
    Task<IReadOnlySet<string>> ForUserAsync(Guid userId, string tenant, CancellationToken cancellationToken = default);
}
