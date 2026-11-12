using System.Security.Claims;
using FastEndpoints;

namespace barakoCMS.Modules;

/// <summary>
/// Endpoint metadata: a read-only API key (<c>bcms_ro_</c>) bound to a service principal may call this
/// route with any method, for rows owned by that same principal. Set it with
/// <see cref="ReadOnlyKeyRoutes.AllowReadOnlyKeySelf"/>. Core registers it on no route.
/// </summary>
public sealed record ReadOnlyKeySelfRoute;

/// <summary>
/// Endpoint metadata: a read-only API key may call this route with any method, because the route is
/// a POST only to carry a body and changes nothing. The key's scopes and content type limits apply as
/// they do to a GET. Set it with <see cref="ReadOnlyKeyRoutes.AllowReadOnlyKeyRead"/>. Core registers
/// it on no route.
/// </summary>
public sealed record ReadOnlyKeyReadRoute;

/// <summary>
/// How a module opens a route to read-only API keys, and how a self-service route finds its owner.
/// </summary>
/// <remarks>
/// <para>
/// Both markers apply to every API key, read-only or not, so a read-only key is never more capable than
/// a full one on a marked route. The self marker skips the key's scope check (a <c>cap:</c> scope
/// included) for any key bound to a service principal that names no content types. Only
/// principal-bound keys gain reach on a self route; a read-only key with no principal does not. The
/// read marker judges the request as a GET: the key's scopes and content type limits still apply.
/// </para>
/// <para>
/// Neither marker widens an unmarked route. A key that was revoked, has expired, or acts as a disabled
/// principal never reaches the route, marked or not.
/// </para>
/// <para>
/// A self route must filter by the tenant as well as by <see cref="TryGetOwnerId"/>. A named rate-limit
/// policy partitioned by <c>User</c> reads the <c>UserId</c> claim, which a principal key does not
/// carry, so partition a self route by API key.
/// </para>
/// </remarks>
public static class ReadOnlyKeyRoutes
{
    /// <summary>The claim a key bound to a service principal carries.</summary>
    public const string PrincipalClaim = "principal_id";

    /// <summary>The claim a read-only key carries, with the value <c>true</c>.</summary>
    public const string ReadOnlyClaim = "apikey_readonly";

    /// <summary>The claim a person, and a key that acts as its creator, carries.</summary>
    public const string UserIdClaim = "UserId";

    /// <summary>
    /// Lets a read-only key that is bound to a principal, and names no content types, call this route
    /// with any method. The endpoint must filter every read and write by
    /// <see cref="TryGetOwnerId"/>: core cannot check that it does.
    /// </summary>
    public static void AllowReadOnlyKeySelf(this EndpointDefinition definition) =>
        definition.Metadata(new ReadOnlyKeySelfRoute());

    /// <summary>
    /// Lets a read-only key call this route whatever the method, with its scopes and content type
    /// limits applied as for a GET. For a route that reads and changes nothing.
    /// </summary>
    public static void AllowReadOnlyKeyRead(this EndpointDefinition definition) =>
        definition.Metadata(new ReadOnlyKeyReadRoute());

    /// <summary>
    /// The id that owns the caller's rows: the principal id for a key bound to a service principal,
    /// the user id for a person. A full (not read-only) key with no principal acts as its creator and
    /// returns the creator's user id. False for a read-only key with no principal and for a caller with
    /// neither, and the endpoint answers 403 then. The principal id wins when both claims are present.
    /// </summary>
    public static bool TryGetOwnerId(this ClaimsPrincipal caller, out Guid ownerId)
    {
        if (Guid.TryParse(caller.FindFirst(PrincipalClaim)?.Value, out ownerId))
            return true;
        if (!caller.HasClaim(ReadOnlyClaim, "true")
            && Guid.TryParse(caller.FindFirst(UserIdClaim)?.Value, out ownerId))
            return true;
        ownerId = Guid.Empty;
        return false;
    }
}
