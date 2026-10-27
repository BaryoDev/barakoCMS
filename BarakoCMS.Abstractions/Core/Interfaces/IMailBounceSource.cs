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

    /// <summary>
    /// Up to <paramref name="limit"/> bounces of <paramref name="tenant"/> after a position, oldest
    /// first, ordered by time and then by id. With <paramref name="afterId"/> null the position is a
    /// time and the bounces are those strictly later. With an id it is the pair, and the bounces are
    /// those after it in that order. The activity feed reads this. The default returns none, so an
    /// implementor written before the feed existed still compiles and the feed shows no bounces for it.
    /// </summary>
    Task<IReadOnlyList<MailBounce>> ListAsync(
        string tenant, DateTimeOffset after, Guid? afterId, int limit, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<MailBounce>>([]);

    /// <summary>The newest <paramref name="limit"/> bounces of <paramref name="tenant"/>, newest first. The default returns none.</summary>
    Task<IReadOnlyList<MailBounce>> ListNewestAsync(
        string tenant, int limit, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<MailBounce>>([]);
}

/// <summary>One bounce as the activity feed shows it.</summary>
/// <param name="Id">Stable across reads.</param>
/// <param name="At">When the provider reported it.</param>
/// <param name="Title">A short label for the message that bounced. Never the recipient address.</param>
public sealed record MailBounce(Guid Id, DateTimeOffset At, string? Title);
