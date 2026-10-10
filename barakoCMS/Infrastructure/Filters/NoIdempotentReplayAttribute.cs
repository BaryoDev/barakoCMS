namespace barakoCMS.Infrastructure.Filters;

/// <summary>
/// Marks an endpoint whose response is never kept for an Idempotency-Key replay, and whose request
/// is never hashed. A retry with a used key is answered 409, as before replay existed.
/// </summary>
/// <remarks>
/// For any route whose response carries a credential (a token, a refresh token, an API key, a TOTP
/// secret, recovery codes, a share link key), whose request carries a password, or whose answer must
/// be decided afresh each time (opening a share link that may since have been revoked). Credentials
/// are kept only as hashes everywhere else, and a stored response or request hash would be a second
/// copy. Public so a module endpoint can carry it.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = true)]
public sealed class NoIdempotentReplayAttribute : Attribute;
