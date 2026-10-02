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

    /// <summary>
    /// Sends an email that belongs to no tenant, with files attached.
    /// </summary>
    /// <remarks>
    /// The default sends nothing and throws <see cref="NotSupportedException"/> when there is an
    /// attachment, so a provider written before attachments existed still compiles and never sends
    /// a message with its files missing.
    /// </remarks>
    Task SendEmailAsync(string to, string subject, string body, IReadOnlyList<EmailAttachment> attachments, CancellationToken cancellationToken = default) =>
        attachments.Count == 0
            ? SendEmailAsync(to, subject, body, cancellationToken)
            : throw new NotSupportedException($"{GetType().Name} does not send attachments.");

    /// <summary>
    /// Sends an email on <paramref name="tenant"/>'s behalf, with files attached. The default ignores
    /// the tenant.
    /// </summary>
    Task SendForTenantAsync(string tenant, string to, string subject, string body, IReadOnlyList<EmailAttachment> attachments, CancellationToken cancellationToken = default) =>
        attachments.Count == 0
            ? SendForTenantAsync(tenant, to, subject, body, cancellationToken)
            : SendEmailAsync(to, subject, body, attachments, cancellationToken);
}

/// <summary>One file attached to an email.</summary>
/// <remarks>
/// The caller hands over a name and a type that are safe to send: the name has no control
/// characters and no path, and the type is one bare media type. A provider puts them in the message
/// through its own encoder and does not have to check them again.
/// </remarks>
public sealed class EmailAttachment
{
    public required string FileName { get; init; }

    public required string ContentType { get; init; }

    public required byte[] Content { get; init; }
}
