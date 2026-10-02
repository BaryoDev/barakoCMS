using FastEndpoints;
using FastEndpoints.Security;
using FluentValidation;
using Marten;
using barakoCMS.Models;
using barakoCMS.Infrastructure.Multitenancy;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace barakoCMS.Features.Me;

/// <remarks>
/// Only the JSON body names the target. FastEndpoints binds the query string onto a request after
/// the body, which would let a URL override or contradict the body on an endpoint that issues a
/// token, so both properties refuse every source but the body.
/// </remarks>
internal class SwitchTenantRequest
{
    private const Source NotTheBody = Source.QueryParam | Source.RouteParam | Source.FormField;

    /// <summary>The handle of the tenant to switch into.</summary>
    [DontBind(NotTheBody)]
    public string? Tenant { get; set; }

    /// <summary>
    /// An alias of <c>tenant</c>, kept for clients written when this was the only name. Send one of
    /// the two: a request where both are set and name different tenants is refused.
    /// </summary>
    [DontBind(NotTheBody)]
    public string? Club { get; set; }
}

/// <summary>
/// The one reading of a switch request. The validator and the endpoint both go through it, so a
/// handle is normalised the same way whichever field carried it.
/// </summary>
internal static class SwitchTarget
{
    public static string Of(SwitchTenantRequest req)
    {
        var tenant = Normalise(req.Tenant);
        return tenant.Length > 0 ? tenant : Normalise(req.Club);
    }

    /// <summary>A blank field counts as not sent, so it cannot disagree with the other one.</summary>
    public static bool Disagree(SwitchTenantRequest req)
    {
        var tenant = Normalise(req.Tenant);
        var club = Normalise(req.Club);
        return tenant.Length > 0 && club.Length > 0 && !string.Equals(tenant, club, StringComparison.Ordinal);
    }

    /// <summary>
    /// True when the request named the tenant through <c>club</c> alone. Its errors are then reported
    /// against <c>club</c>, where a client that only knows that field looks for them.
    /// </summary>
    public static bool CameFromAlias(SwitchTenantRequest req) =>
        Normalise(req.Tenant).Length == 0 && req.Club is not null;

    private static string Normalise(string? handle) => (handle ?? string.Empty).Trim().ToLowerInvariant();
}

internal class SwitchTenantResponse
{
    public string Token { get; set; } = string.Empty;
    public DateTime Expiry { get; set; }
    /// <summary>Always empty. Kept so the response keeps its shape.</summary>
    public string RefreshToken { get; set; } = string.Empty;

    /// <summary>Always the default value. Kept so the response keeps its shape.</summary>
    public DateTime RefreshTokenExpiry { get; set; }
}

internal class SwitchTenantValidator : Validator<SwitchTenantRequest>
{
    public SwitchTenantValidator()
    {
        // Disagreeing needs a tenant that is not blank, so it never falls under the club rule below.
        RuleFor(x => x.Tenant)
            .Must((req, _) => !SwitchTarget.Disagree(req))
            .WithMessage("tenant and club name different tenants. Send one of them.")
            .Must((req, _) => SwitchTarget.Of(req).Length > 0)
            .WithMessage("A tenant is required.")
            .When(req => !SwitchTarget.CameFromAlias(req));

        RuleFor(x => x.Club)
            .Must((req, _) => SwitchTarget.Of(req).Length > 0)
            .WithMessage("A tenant is required.")
            .When(req => SwitchTarget.CameFromAlias(req));
    }
}

/// <summary>
/// POST /api/me/switch: the signed-in user exchanges their access token for one scoped to another
/// tenant, without re-authenticating. The issuer checks for an active membership in the target tenant
/// (or treats it as unmanaged, as every token path does) and bakes that tenant's roles into the new
/// token. Device binding (the <c>did</c> claim) is carried over so device-trust still holds.
/// </summary>
/// <remarks>
/// It exchanges, it does not renew. The new token expires when the presented one would have, and the
/// presented one is revoked, so a chain of switches cannot keep a bearer alive past its original
/// expiry. It returns no refresh token: the one the caller already holds is not tied to a tenant, since
/// a refresh mints for the <c>X-Tenant</c> it is sent and re-checks membership.
/// </remarks>
internal class SwitchTenantEndpoint : Endpoint<SwitchTenantRequest, SwitchTenantResponse>
{
    private readonly IDocumentSession _session;

    private readonly barakoCMS.Infrastructure.Auth.ITokenIssuer _tokenIssuer;
    private readonly barakoCMS.Infrastructure.Services.ITokenRevocationService _revocation;

    public SwitchTenantEndpoint(
        IDocumentSession session,
        barakoCMS.Infrastructure.Auth.ITokenIssuer tokenIssuer,
        barakoCMS.Infrastructure.Services.ITokenRevocationService revocation)
    {
        _session = session;
        _tokenIssuer = tokenIssuer;
        _revocation = revocation;
    }

    public override void Configure()
    {
        Post("/api/me/switch"); // authenticated by default
    }

    public override async Task HandleAsync(SwitchTenantRequest req, CancellationToken ct)
    {
        var target = SwitchTarget.Of(req);

        Guid.TryParse(User.FindFirst("UserId")?.Value, out var userId);
        var user = await _session.Query<User>().FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        // Carry the device binding forward so DeviceTrust enforcement keeps working after a switch.
        var did = User.FindFirst("did")?.Value;
        var extraClaims = new List<Claim>();
        if (!string.IsNullOrEmpty(did))
            extraClaims.Add(new("did", did));

        // This endpoint already performed the membership check correctly and was the model for
        // ITokenIssuer; it now delegates so there is exactly one implementation to keep right.
        // The bearer middleware refuses a token without exp, so it is always here.
        if (!long.TryParse(User.FindFirst(JwtRegisteredClaimNames.Exp)?.Value, out var expUnix))
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }
        var presentedExpiry = DateTimeOffset.FromUnixTimeSeconds(expUnix).UtcDateTime;

        var issued = await _tokenIssuer.IssueAccessTokenAsync(user, target, extraClaims, presentedExpiry, ct);
        if (!issued.Allowed)
        {
            const string notAMember = "You are not a member of this tenant.";
            if (SwitchTarget.CameFromAlias(req))
                ThrowError(r => r.Club, notAMember);
            ThrowError(r => r.Tenant, notAMember);
            return;
        }

        var presentedJti = User.FindFirst(JwtRegisteredClaimNames.Jti)?.Value;
        if (!string.IsNullOrEmpty(presentedJti))
            await _revocation.RevokeTokenAsync(presentedJti, user.Id, "switched", presentedExpiry, ct);

        await Send.ResponseAsync(new SwitchTenantResponse
        {
            Token = issued.Token,
            Expiry = issued.ExpiresAt,
        });
    }
}
