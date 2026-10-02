using System.Security.Cryptography;
using System.Text;

namespace BarakoCMS.ExternalAuth;

/// <summary>
/// Ties one account at an identity provider to one local user. Global, like the user it points at.
/// </summary>
/// <remarks>
/// The provider's account is named by issuer and subject together, which is the only pair OpenID
/// Connect promises is stable and unique. An email address is neither: it can be changed at the
/// provider, and reassigned to somebody else. So the address is consulted once, to decide which
/// local user a first sign-in belongs to, and never again.
/// </remarks>
public class ExternalIdentity
{
    /// <summary><see cref="KeyOf"/> of the issuer and subject, so the primary key is the uniqueness rule.</summary>
    public string Id { get; set; } = string.Empty;

    public string Issuer { get; set; } = string.Empty;

    public string Subject { get; set; } = string.Empty;

    public Guid UserId { get; set; }

    /// <summary>The configured provider name the link was made through. For support, not for lookup.</summary>
    public string Provider { get; set; } = string.Empty;

    public DateTime LinkedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// SHA-256 of the issuer and subject, each prefixed with its length so no two pairs can be
    /// joined into the same bytes. Compared exactly as given: issuer and subject are case sensitive.
    /// </summary>
    public static string KeyOf(string issuer, string subject) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{issuer.Length}:{issuer}|{subject.Length}:{subject}")));
}
