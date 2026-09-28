namespace BarakoCMS.Email.Resend;

/// <summary>
/// Which tenant sent an email, keyed by the id Resend gave it, so a bounce reported later can be
/// put back on that tenant.
/// </summary>
/// <remarks>
/// Resend's webhook names the email and the recipient and nothing else. The recipient is no guide:
/// one address can belong to members of several tenants, and any tenant administrator can add an
/// existing account as a member.
/// </remarks>
public class SentEmail
{
    /// <summary>Resend's email id.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>The tenant the send ran in, lowercased like every resolved tenant slug.</summary>
    public string Tenant { get; set; } = string.Empty;

    public DateTime At { get; set; } = DateTime.UtcNow;
}
