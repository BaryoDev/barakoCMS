namespace barakoCMS.Core.Interfaces;

public interface IEmailService
{
    /// <summary>
    /// Sends an email that belongs to no tenant: sign-in codes, verification, lockout notices and
    /// anything else sent to a user about their own account.
    /// </summary>
    Task SendEmailAsync(string to, string subject, string body, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends an email on <paramref name="tenant"/>'s behalf, such as a workflow's notification, so a
    /// provider that reports delivery problems can put them back on that tenant.
    /// </summary>
    /// <remarks>
    /// Explicit rather than read from the ambient tenant, because every request resolves some tenant,
    /// including the ones that send a user's own sign-in code. The default ignores the tenant, so an
    /// existing provider keeps working unchanged.
    /// </remarks>
    Task SendForTenantAsync(string tenant, string to, string subject, string body, CancellationToken cancellationToken = default) =>
        SendEmailAsync(to, subject, body, cancellationToken);
}
