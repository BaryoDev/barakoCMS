namespace barakoCMS.Models;

/// <summary>
/// The email provider credentials an operator entered in the admin, rather than ones an engineer put
/// in the deployment.
/// </summary>
/// <remarks>
/// A document of its own rather than rows in <see cref="SystemSetting"/>, which is where settings
/// otherwise live. <c>GET /api/settings</c> returns every setting's value, and
/// <c>POST /api/settings</c> takes any key and stores the string it is given, so putting a
/// credential there means it is written in plaintext and handed back to every admin who opens the
/// settings page. Refusing to return one value out of that list is a filter somebody has to
/// remember for every secret added afterwards. A typed document with no plaintext field cannot leak
/// the key, because there is nothing to leak.
///
/// One row, at <see cref="SingletonId"/>. A settings document with an unconstrained key invites the
/// question of which row wins, and there is no answer to it that an operator would enjoy finding out.
/// </remarks>
public class EmailSettings
{
    /// <summary>There is exactly one of these, so its id is fixed rather than generated.</summary>
    public static readonly Guid SingletonId = new("8d0d5f6a-2c1e-4a7b-9f3d-6b1c2e4a7d90");

    public Guid Id { get; set; } = SingletonId;

    /// <summary>The provider API key, encrypted with <c>ISecretProtector</c>. Never the plaintext.</summary>
    public string ProtectedApiKey { get; set; } = string.Empty;

    /// <summary>The address email is sent from, which is not a secret.</summary>
    public string FromAddress { get; set; } = string.Empty;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Who last changed it. The audit trail carries the same, and outlives this row.</summary>
    public string? UpdatedBy { get; set; }
}
