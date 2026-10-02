namespace BarakoCMS.Forms;

/// <summary>
/// The one-time code last sent to one email address in one tenant, and how many were sent to it
/// lately.
/// </summary>
/// <remarks>
/// One row per address, so a new code replaces the old one and the row count is bounded by the
/// addresses asked for inside the window. The address itself is not stored: <see cref="Id"/> is a
/// SHA-256 of it, which is all a lookup needs. The code is not stored either, only its BCrypt hash,
/// the at-rest rule <c>OtpCode.CodeHash</c> follows.
/// </remarks>
public sealed class FormEmailVerification
{
    /// <summary>Hex SHA-256 of the address, trimmed and lowercased.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>The form the live code was sent for. A code is refused on any other form.</summary>
    public string Form { get; set; } = string.Empty;

    /// <summary>Null once the code was used, or was guessed at too often.</summary>
    public string? CodeHash { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Checks made against the live code, right or wrong.</summary>
    public int Attempts { get; set; }

    public DateTimeOffset WindowStartedAt { get; set; }

    /// <summary>Codes sent to this address since <see cref="WindowStartedAt"/>.</summary>
    public int Sent { get; set; }

    public DateTimeOffset LastSentAt { get; set; }
}

/// <summary>How many codes one form has sent lately, across every address.</summary>
public sealed class FormEmailBudget
{
    /// <summary>The form's content type name.</summary>
    public string Id { get; set; } = string.Empty;

    public DateTimeOffset WindowStartedAt { get; set; }

    /// <summary>Codes sent for this form since <see cref="WindowStartedAt"/>.</summary>
    public int Sent { get; set; }
}
