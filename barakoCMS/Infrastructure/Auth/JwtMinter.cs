using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace barakoCMS.Infrastructure.Auth;

/// <summary>
/// Signs the JWTs this host issues: access tokens and the signed grants that ride beside them
/// (MFA challenge, step-up, preview, viewer). One per host, from its own DI.
/// </summary>
/// <remarks>
/// Replaces <c>JwtBearer.CreateToken</c>, which reads its defaults and its clock from FastEndpoints'
/// process-wide resolver, so a second host in the process changed what the first one issued. This
/// reads the key and issuer from the host's configuration and the clock from the host's
/// <see cref="TimeProvider"/>, and builds the descriptor <c>JwtBearer.CreateToken</c> builds.
/// It takes every setting from those two, so an <c>IOptions&lt;JwtCreationOptions&gt;</c>
/// registered in the host would be ignored; a test fails if one is.
/// </remarks>
public interface IJwtMinter
{
    /// <summary>
    /// A compact JWT signed with <c>JWT:Key</c> (HS256) and issued by <c>JWT:Issuer</c>, carrying
    /// <paramref name="claims"/> in the order given.
    /// </summary>
    /// <param name="audience">The <c>aud</c> claim, or null for none.</param>
    /// <param name="expiresAt">The <c>exp</c> claim, in UTC.</param>
    /// <param name="claims">The subject's claims.</param>
    /// <exception cref="InvalidOperationException">No <c>JWT:Key</c> is set, or it is too short or a shipped placeholder.</exception>
    string Mint(string? audience, DateTime expiresAt, IEnumerable<Claim> claims);
}

/// <inheritdoc cref="IJwtMinter"/>
public sealed class JwtMinter(IConfiguration config, TimeProvider clock, Microsoft.Extensions.Hosting.IHostEnvironment? environment = null)
    : IJwtMinter
{
    public string Mint(string? audience, DateTime expiresAt, IEnumerable<Claim> claims)
    {
        var key = JwtKeySource.Resolve(config, Security.DevelopmentHost.Of(environment));

        // The same descriptor JwtBearer.CreateToken builds in FastEndpoints.Security 8.3.0: the key
        // as ASCII bytes (the encoding bearer validation uses), HS256, the claims in the order
        // given. NotBefore is left unset on purpose. The handler fills it from the system clock,
        // and so did the old path, so the bytes stay what they were.
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = config["JWT:Issuer"],
            Audience = audience,
            IssuedAt = clock.GetUtcNow().UtcDateTime,
            Subject = new ClaimsIdentity(claims.ToList()),
            Expires = expiresAt,
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(JwtKeySource.Bytes(key)), SecurityAlgorithms.HmacSha256),
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }
}
