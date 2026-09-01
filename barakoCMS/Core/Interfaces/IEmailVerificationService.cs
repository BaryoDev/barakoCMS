namespace barakoCMS.Core.Interfaces;

/// <summary>
/// Issues the tokens that turn a self-registration into an account. Sits beside
/// <see cref="IOtpService"/>, which does the same job for sign-in codes.
/// </summary>
public interface IEmailVerificationService
{
    /// <summary>
    /// Records a pending registration and emails its verification token to
    /// <paramref name="email"/>.
    /// </summary>
    /// <param name="passwordHash">Already hashed by the caller. This never sees a plaintext password.</param>
    /// <returns>
    /// False when the record was stored but could not be emailed. The register endpoint deliberately
    /// does not pass that on to the caller, because the alternative answers differ between an address
    /// that is already registered and one that is not, which is the enumeration this whole endpoint
    /// is careful about. It is logged at Error instead.
    /// </returns>
    Task<bool> IssueAsync(string username, string email, string passwordHash, CancellationToken ct);

    /// <summary>
    /// Emails the owner of an already-registered address that somebody tried to register it again.
    /// </summary>
    /// <remarks>
    /// Not politeness. The register endpoint has to answer identically whether or not the address is
    /// known, and an endpoint that emails in one case and not the other answers identically in the
    /// body while taking visibly different time to do it. Sending on both paths equalises the work,
    /// and it is also the only useful signal available: the person who owns the mailbox is the one
    /// who should hear that somebody tried.
    /// </remarks>
    Task<bool> SendAlreadyRegisteredAsync(string email, CancellationToken ct);
}
