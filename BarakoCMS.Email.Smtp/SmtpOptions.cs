namespace BarakoCMS.Email.Smtp;

/// <summary>
/// Everything this module needs to reach a relay, read from its own configuration section
/// <c>Modules:Email.Smtp</c>.
/// </summary>
/// <remarks>
/// These do not come from <c>IEmailSettingsProvider</c>, which resolves an API key and a from
/// address and nothing else. SMTP needs a host, a port, a user, a password and a TLS mode, and
/// widening that interface to carry them is a change to the core contract rather than a new module.
/// So the credentials live here, in the section the module already owns, and the consequence is
/// written down in the README: SMTP credentials are a deployment decision, not an admin one.
/// </remarks>
public sealed class SmtpOptions
{
    /// <summary>The module's own configuration section.</summary>
    public const string SectionName = "Modules:Email.Smtp";

    /// <summary>
    /// The relay's hostname. Nothing here means the module registers no provider at all, so an
    /// upgrade that configures nothing keeps whatever it had.
    /// </summary>
    public string? Host { get; set; }

    /// <summary>Submission port. 587 is the standard one, 465 the implicit-TLS one.</summary>
    public int Port { get; set; } = 587;

    /// <summary>Username, when the relay wants one. An unauthenticated relay leaves this empty.</summary>
    public string? User { get; set; }

    /// <summary>Password for <see cref="User"/>. Never logged, and never in an error message.</summary>
    public string? Password { get; set; }

    /// <summary>
    /// Sender address, e.g. <c>MyApp &lt;no-reply@example.com&gt;</c>. A from address stored in the
    /// admin wins over this, because that field is provider-neutral and somebody typed it in.
    /// </summary>
    public string? From { get; set; }

    /// <summary>
    /// How the connection is secured. Unset picks by port: implicit TLS on 465, STARTTLS everywhere
    /// else.
    /// </summary>
    public SmtpSecurity? Security { get; set; }
}

/// <summary>How the connection to the relay is secured.</summary>
public enum SmtpSecurity
{
    /// <summary>
    /// Plaintext. Only for a relay reached over a network you already trust, and it has to be asked
    /// for by name: a default that quietly falls back to this would send credentials in the clear on
    /// a server that simply forgot to advertise STARTTLS.
    /// </summary>
    None,

    /// <summary>Connect in the clear, then upgrade, and fail if the relay will not. The 587 default.</summary>
    StartTls,

    /// <summary>TLS from the first byte. The 465 default.</summary>
    SslOnConnect,
}
