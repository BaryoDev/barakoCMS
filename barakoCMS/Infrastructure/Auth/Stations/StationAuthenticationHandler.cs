using System.Security.Claims;
using System.Text.Encodings.Web;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using Marten;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace barakoCMS.Infrastructure.Auth.Stations;

public sealed class StationAuthenticationOptions : AuthenticationSchemeOptions;

/// <summary>
/// Authenticates station keys (<c>bsta_...</c>). The principal has no <c>UserId</c>: a station is not
/// a person, holds no roles, and gets its capabilities from the key's own list, carried as
/// <see cref="CapabilityClaim"/> claims.
/// </summary>
/// <remarks>
/// The station is read from the store on every request, never cached, so a revoke takes effect on
/// the next request. Like the API key handler, it reads through a throwaway session and sets
/// <see cref="TenantContext"/> to the station's tenant before any content session opens, so the
/// tenant comes from the key and not from whatever X-Tenant the caller sent.
/// </remarks>
public sealed class StationAuthenticationHandler : AuthenticationHandler<StationAuthenticationOptions>
{
    public const string SchemeName = "Station";
    public const string CapabilityClaim = "station_cap";
    public const string IdClaim = "StationId";

    /// <summary>
    /// How long one authentication stays good, matching the 15 minute access token. A station key
    /// never expires, so without this a hub connection (mapped with CloseOnAuthenticationExpiration)
    /// would never close and a revoked station would hear notices until it chose to disconnect. At
    /// expiry the connection closes, the client reconnects, and this handler refuses a revoked key.
    /// </summary>
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(15);

    public StationAuthenticationHandler(
        IOptionsMonitor<StationAuthenticationOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder) { }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var key = StationKeys.Presented(Request);
        if (key is null) return AuthenticateResult.NoResult();

        var store = Context.RequestServices.GetRequiredService<IDocumentStore>();
        var ct = Context.RequestAborted;
        await using var lookup = store.QuerySession();

        var hash = StationKeys.Hash(key);
        var station = await lookup.Query<Station>().FirstOrDefaultAsync(s => s.KeyHash == hash, ct);

        // One message for every refusal, so a prober cannot tell a revoked key from a made-up one.
        if (station is null || station.Revoked) return Fail();

        var slug = station.TenantSlug;
        if (await Context.RequestServices.GetRequiredService<TenancyOptions>().RefusesAsync(lookup, slug, ct))
            return Fail();
        if (await lookup.Query<Tenant>().FirstOrDefaultAsync(t => t.Slug == slug, ct) is { IsActive: false })
            return Fail();

        Context.RequestServices.GetRequiredService<TenantContext>().Slug = slug;

        var claims = new List<Claim>
        {
            new(IdClaim, station.Id.ToString()),
            new("Username", $"station:{station.Name}"),
            new("tenant", slug),
            new("auth_method", "station"),
        };
        foreach (var capability in station.Capabilities)
            claims.Add(new Claim(CapabilityClaim, capability));

        var identity = new ClaimsIdentity(claims, SchemeName, "Username", ClaimTypes.Role);
        var now = DateTimeOffset.UtcNow;
        var properties = new AuthenticationProperties { IssuedUtc = now, ExpiresUtc = now + SessionLifetime };
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), properties, SchemeName));
    }

    private static AuthenticateResult Fail() => AuthenticateResult.Fail("Invalid station key");
}
