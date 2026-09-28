namespace BarakoCMS.Email.Resend;

/// <summary>
/// A delivery problem Resend reported for an address we emailed (bounce, complaint, delay). Global
/// (not tenant-scoped) — keyed by recipient email, since the webhook carries no tenant context.
/// Apps match the address to their own records.
/// </summary>
/// <remarks>
/// <see cref="Tenant"/> comes from the <see cref="SentEmail"/> recorded when the email went out.
/// It is null for an event about an email sent before sends were recorded, and for one sent by
/// something other than this deployment.
/// </remarks>
public class EmailEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Email { get; set; } = string.Empty; // recipient, lowercased
    public string Type { get; set; } = string.Empty;  // bounced | complained | delivery_delayed
    public string Reason { get; set; } = string.Empty;
    public DateTime At { get; set; } = DateTime.UtcNow;
    public string EmailId { get; set; } = string.Empty; // Resend email id
    public string? Tenant { get; set; }
}
