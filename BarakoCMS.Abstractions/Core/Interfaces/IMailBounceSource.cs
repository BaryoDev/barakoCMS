namespace barakoCMS.Core.Interfaces;

/// <summary>
/// Where an email module keeps the bounces its provider reported, so the overview can count them and
/// let a person mark them seen. Optional: with no implementation registered, the overview leaves mail
/// out rather than reporting it as healthy.
/// </summary>
/// <remarks>
/// Every member takes the tenant explicitly. A provider is one account for the deployment, and a
/// bounce belongs to the tenant that sent the message.
/// </remarks>
public interface IMailBounceSource
{
    /// <summary>Bounces recorded for <paramref name="tenant"/> at or after <paramref name="from"/> and before <paramref name="to"/>.</summary>
    Task<int> CountAsync(string tenant, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default);

    /// <summary>Bounces for <paramref name="tenant"/> that nobody has marked resolved.</summary>
    Task<int> CountUnresolvedAsync(string tenant, CancellationToken cancellationToken = default);

    /// <summary>Marks every unresolved bounce of <paramref name="tenant"/> resolved. Returns how many it marked.</summary>
    Task<int> ResolveAllAsync(string tenant, CancellationToken cancellationToken = default);
}
