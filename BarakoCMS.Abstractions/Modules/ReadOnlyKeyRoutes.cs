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
/// A module that marks a route widens what every read-only key on that instance can do, so mark a
/// route only when it is meant for them. Neither marker widens an unmarked route. A key that was
/// revoked, has expired, or acts as a disabled principal never reaches the route, marked or not.
/// </remarks>
public static class ReadOnlyKeyRoutes
{
    /// <summary>The claim a key bound to a service principal carries.</summary>
    public const string PrincipalClaim = "principal_id";

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
    /// the user id for a person. False for a read-only key with no principal and for a caller with
    /// neither, and the endpoint answers 403 then.
    /// </summary>
    public static bool TryGetOwnerId(this ClaimsPrincipal caller, out Guid ownerId)
    {
        if (Guid.TryParse(caller.FindFirst(PrincipalClaim)?.Value, out ownerId))
            return true;
        if (!caller.HasClaim("apikey_readonly", "true")
            && Guid.TryParse(caller.FindFirst("UserId")?.Value, out ownerId))
            return true;
        ownerId = Guid.Empty;
        return false;
    }
}
