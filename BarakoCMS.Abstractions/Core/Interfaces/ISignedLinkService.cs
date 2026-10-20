namespace barakoCMS.Core.Interfaces;

/// <summary>Why a signed link was refused.</summary>
public enum SignedLinkFailure
{
    /// <summary>The link is valid.</summary>
    None,
    /// <summary>Not a link this service made, or the signature does not hold for this purpose.</summary>
    Invalid,
    /// <summary>The link ran out.</summary>
    Expired,
    /// <summary>A single use link that was already redeemed.</summary>
    AlreadyUsed,
    /// <summary>The link was revoked.</summary>
    Revoked,
}

/// <summary>A link that was just issued.</summary>
/// <param name="Token">The value to hand to the visitor.</param>
/// <param name="TokenId">Names the link, for <see cref="ISignedLinkService.RevokeAsync"/>.</param>
/// <param name="ExpiresAt">When the link stops validating.</param>
public sealed record SignedLink(string Token, string TokenId, DateTimeOffset ExpiresAt);

/// <summary>The answer to validating a link.</summary>
/// <param name="Subject">What the link was issued for. Null unless the link is valid.</param>
/// <param name="Failure"><see cref="SignedLinkFailure.None"/> when valid.</param>
public sealed record SignedLinkResult(string? Subject, SignedLinkFailure Failure)
{
    /// <summary>True when the link validated and <see cref="Subject"/> is set.</summary>
    public bool Succeeded => Failure == SignedLinkFailure.None;
}

/// <summary>
/// Signed, expiring links for anything a visitor follows without signing in: a preview, a share, a
/// confirmation. Each link belongs to a purpose. The signing key is derived from the host's signing
/// secret with the purpose as context, so a link issued for one purpose never validates for another.
/// </summary>
public interface ISignedLinkService
{
    /// <summary>Makes a link that names <paramref name="subject"/> and lasts <paramref name="lifetime"/>.</summary>
    /// <param name="purpose">What the link is for, for example <c>preview</c>. Not empty.</param>
    /// <param name="subject">What the link points at. Returned by <see cref="ValidateAsync"/>.</param>
    /// <param name="lifetime">How long the link validates. Positive.</param>
    /// <param name="singleUse">When true, the first successful validation uses the link up.</param>
    SignedLink Issue(string purpose, string subject, TimeSpan lifetime, bool singleUse = false);

    /// <summary>
    /// Checks <paramref name="token"/> against <paramref name="purpose"/>. A single use link is
    /// redeemed by the call that succeeds, and every later call is refused.
    /// </summary>
    Task<SignedLinkResult> ValidateAsync(string purpose, string token, CancellationToken ct = default);

    /// <summary>Makes the link stop validating. Revoking a link that was never validated is fine.</summary>
    Task RevokeAsync(string tokenId, CancellationToken ct = default);
}
