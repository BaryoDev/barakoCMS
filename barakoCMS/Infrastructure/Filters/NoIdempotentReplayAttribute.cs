namespace barakoCMS.Infrastructure.Filters;

/// <summary>
/// Marks an endpoint whose response is never kept for an Idempotency-Key replay. A retry with a
/// used key is answered 409, as before replay existed, and no response body is stored.
/// </summary>
/// <remarks>
/// For any route whose response carries a credential: a token, a refresh token, an API key, a TOTP
/// secret, recovery codes, a share link key. Those are kept only as hashes everywhere else, and a
/// stored response would be a second copy of the secret. Public so a module endpoint can carry it.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = true)]
public sealed class NoIdempotentReplayAttribute : Attribute;
