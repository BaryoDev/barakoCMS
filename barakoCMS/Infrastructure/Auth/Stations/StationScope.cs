using System.Security.Claims;
using FastEndpoints;
using Marten;

namespace barakoCMS.Infrastructure.Auth.Stations;

/// <summary>Endpoint metadata: stations may call this endpoint.</summary>
public sealed record StationAccess;

public static class StationScope
{
    /// <summary>
    /// Lets station keys call this endpoint. The endpoint's own capability checks still apply,
    /// answered from the station's capability list.
    /// </summary>
    public static void AllowStations(this EndpointDefinition definition) =>
        definition.Metadata(new StationAccess());

    public static bool IsStation(this ClaimsPrincipal user) => user.HasClaim("auth_method", "station");

    /// <summary>
    /// Whether this station principal's key is now revoked or gone. For a connection that stays open
    /// after authentication, such as the realtime hub, whose principal was built when it connected.
    /// False for a caller that is not a station.
    /// </summary>
    public static async Task<bool> IsRevokedStationAsync(this ClaimsPrincipal user, IDocumentStore store, CancellationToken ct)
    {
        if (!user.IsStation()) return false;
        if (!Guid.TryParse(user.FindFirst(StationAuthenticationHandler.IdClaim)?.Value, out var id)) return true;

        await using var lookup = store.QuerySession();
        var station = await lookup.LoadAsync<barakoCMS.Models.Station>(id, ct);
        return station is null || station.Revoked;
    }
}

/// <summary>
/// Refuses a station on every endpoint that did not opt in with <see cref="StationScope.AllowStations"/>.
/// Closed by default, so a new endpoint is never reachable by a kiosk key by accident.
/// </summary>
public sealed class StationScopeProcessor : IGlobalPreProcessor
{
    public async Task PreProcessAsync(IPreProcessorContext context, CancellationToken ct)
    {
        var http = context.HttpContext;
        if (http.Response.HasStarted) return;
        if (!http.User.IsStation()) return;
        if (http.GetEndpoint()?.Metadata.GetMetadata<StationAccess>() is not null) return;

        http.Response.StatusCode = StatusCodes.Status403Forbidden;
        await http.Response.WriteAsync("Station keys cannot use this endpoint.", ct);
    }
}
