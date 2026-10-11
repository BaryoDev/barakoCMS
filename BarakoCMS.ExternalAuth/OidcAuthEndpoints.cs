using FastEndpoints;
using Marten;
// RequireRateLimiting lives here; this project is not a Web SDK project, so it is not implicitly used.
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace BarakoCMS.ExternalAuth;

/// <summary>
/// GET /api/auth/oidc/{name}/start?club={handle}. Sends the browser to the provider configured
/// under <c>Oidc:Providers:{name}</c>.
/// </summary>
/// <remarks>
/// Three secrets are minted and left in one HttpOnly cookie: the <c>state</c> that ties the callback
/// to this browser, the <c>nonce</c> that ties the id token to this request, and the PKCE verifier
/// that ties the code exchange to it. The provider is sent the first two and only a hash of the
/// third. The <c>redirect_uri</c> is built from the configured base URL and the configured provider
/// name, and nothing in the request can change where the browser is sent back to.
/// </remarks>
internal sealed class OidcStartEndpoint(IConfiguration config, OidcBackchannel backchannel) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/api/auth/oidc/{name}/start");
        AllowAnonymous();
        Options(x => x.RequireRateLimiting(OidcSupport.RateLimitPolicy));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var provider = OidcProviders.Find(config, Route<string>("name"));
        var club = (Query<string>("club", isRequired: false) ?? "").Trim().ToLowerInvariant();
        if (provider is null || club.Length > OidcSupport.MaxClubLength)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var redirect = OidcSupport.CallbackUrl(config, HttpContext, provider);
        var endpoints = await backchannel.EndpointsAsync(provider, ct);
        if (endpoints is null)
        {
            var to = $"{ExternalAuthSupport.BaseUrl(config, HttpContext)}/login?fberror="
                + Uri.EscapeDataString($"Couldn't reach {provider.DisplayName}. Please try again, or use your email code.");
            if (club.Length > 0) to += $"&club={Uri.EscapeDataString(club)}";
            await Send.ResultAsync(Results.Redirect(to));
            return;
        }

        var flow = OidcFlow.New();
        HttpContext.Response.Cookies.Append(provider.StateCookie, flow.ToCookie(), OidcSupport.FlowCookie(provider));
        HttpContext.Response.Cookies.Append(provider.ClubCookie, club, OidcSupport.FlowCookie(provider));

        var separator = endpoints.AuthorizationEndpoint.Contains('?') ? '&' : '?';
        var url =
            $"{endpoints.AuthorizationEndpoint}{separator}response_type=code" +
            $"&client_id={Uri.EscapeDataString(provider.ClientId)}" +
            $"&redirect_uri={Uri.EscapeDataString(redirect)}" +
            $"&scope={Uri.EscapeDataString(provider.Scopes)}" +
            (provider.FormPost ? "&response_mode=form_post" : "") +
            $"&state={flow.State}&nonce={flow.Nonce}" +
            $"&code_challenge={flow.CodeChallenge}&code_challenge_method=S256";
        await Send.ResultAsync(Results.Redirect(url));
    }
}

/// <summary>
/// GET /api/auth/oidc/{name}/callback. Where a provider redirects back to, with the code and state
/// in the query.
/// </summary>
internal sealed class OidcCallbackEndpoint(
    IDocumentSession session,
    IConfiguration config,
    OidcBackchannel backchannel,
    OidcConsumedStates consumed,
    barakoCMS.Core.Interfaces.IDeviceGate deviceGate,
    barakoCMS.Infrastructure.Auth.ITokenIssuer tokenIssuer,
    ILogger<OidcCallback> logger)
    : OidcCallback(session, config, backchannel, consumed, deviceGate, tokenIssuer, logger)
{
    public override void Configure()
    {
        Get("/api/auth/oidc/{name}/callback");
        AllowAnonymous();
        Options(x => x.RequireRateLimiting(OidcSupport.RateLimitPolicy));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var provider = OidcProviders.Find(AppConfig, Route<string>("name"));
        if (provider is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await CompleteAsync(
            provider, Query<string>("code", isRequired: false), Query<string>("state", isRequired: false), ct);
    }
}

/// <summary>
/// POST /api/auth/oidc/{name}/callback. Where a provider configured with <c>ResponseMode</c>
/// <c>form_post</c> (Apple) posts the code and state back, as a form.
/// </summary>
/// <remarks>
/// Only for those providers: for any other this route does not exist. The state is checked exactly
/// as on the GET. The body is read only as a URL-encoded form of at most
/// <see cref="MaxFormBytes"/>, and a field sent twice counts as missing. Apple also posts an
/// <c>id_token</c>, which is ignored: the one that is validated is the one the code exchange returns.
/// </remarks>
[barakoCMS.Infrastructure.Filters.NoIdempotentReplay]
internal sealed class OidcFormPostCallbackEndpoint(
    IDocumentSession session,
    IConfiguration config,
    OidcBackchannel backchannel,
    OidcConsumedStates consumed,
    barakoCMS.Core.Interfaces.IDeviceGate deviceGate,
    barakoCMS.Infrastructure.Auth.ITokenIssuer tokenIssuer,
    ILogger<OidcCallback> logger)
    : OidcCallback(session, config, backchannel, consumed, deviceGate, tokenIssuer, logger)
{
    internal const int MaxFormBytes = 64 * 1024;

    public override void Configure()
    {
        Post("/api/auth/oidc/{name}/callback");
        AllowAnonymous();
        AllowFormData(urlEncoded: true);
        MaxRequestBodySize(MaxFormBytes);
        Options(x => x.RequireRateLimiting(OidcSupport.RateLimitPolicy));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var provider = OidcProviders.Find(AppConfig, Route<string>("name"));
        if (provider is null || !provider.FormPost)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        string? code = null;
        string? state = null;
        if (HttpContext.Request.HasFormContentType)
        {
            try
            {
                var form = await HttpContext.Request.ReadFormAsync(ct);
                code = form["code"].Count == 1 ? form["code"][0] : null;
                state = form["state"].Count == 1 ? form["state"][0] : null;
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or BadHttpRequestException)
            {
                // Too long, or not a form. Both leave code and state missing, which fails the flow.
            }
        }

        await CompleteAsync(provider, code, state, ct);
    }
}

/// <summary>
/// The callback, by either method. Checks the state, exchanges the code, validates the id token and
/// hands the identity to <see cref="SocialSignIn"/>.
/// </summary>
/// <remarks>
/// Whatever happens, the browser is sent to the configured base URL: the sign-in page with a
/// message, or the console's social callback with the tokens in the fragment. There is no return
/// address in the request to honour.
/// </remarks>
internal abstract class OidcCallback(
    IDocumentSession session,
    IConfiguration config,
    OidcBackchannel backchannel,
    OidcConsumedStates consumed,
    barakoCMS.Core.Interfaces.IDeviceGate deviceGate,
    barakoCMS.Infrastructure.Auth.ITokenIssuer tokenIssuer,
    ILogger<OidcCallback> logger) : EndpointWithoutRequest
{
    protected IConfiguration AppConfig => config;

    protected async Task CompleteAsync(OidcProvider provider, string? code, string? state, CancellationToken ct)
    {
        var baseUrl = ExternalAuthSupport.BaseUrl(config, HttpContext);
        var club = (HttpContext.Request.Cookies[provider.ClubCookie] ?? "").Trim().ToLowerInvariant();
        if (club.Length > OidcSupport.MaxClubLength)
        {
            club = "";
        }

        var flow = OidcFlow.FromCookie(HttpContext.Request.Cookies[provider.StateCookie]);
        OidcSupport.Expire(HttpContext.Response, provider, provider.StateCookie);
        OidcSupport.Expire(HttpContext.Response, provider, provider.ClubCookie);

        async Task Fail(string message)
        {
            var to = $"{baseUrl}/login?fberror={Uri.EscapeDataString(message)}";
            if (!string.IsNullOrEmpty(club)) to += $"&club={Uri.EscapeDataString(club)}";
            await Send.ResultAsync(Results.Redirect(to));
        }

        var expired = $"{provider.DisplayName} sign-in was cancelled or the link expired. Please try again.";
        var unreachable = $"Couldn't reach {provider.DisplayName}. Please try again, or use your email code.";

        if (flow is null
            || string.IsNullOrEmpty(code) || code.Length > OidcSupport.MaxCodeLength
            || string.IsNullOrEmpty(state) || !OidcSupport.TokenPattern().IsMatch(state)
            || !OidcSupport.FixedTimeEquals(state, flow.State))
        {
            await Fail(expired);
            return;
        }

        if (!consumed.TryConsume(flow.State))
        {
            logger.LogWarning("OIDC provider {Provider}: a callback reused a state that was already spent", provider.Name);
            await Fail(expired);
            return;
        }

        var endpoints = await backchannel.EndpointsAsync(provider, ct);
        if (endpoints is null)
        {
            await Fail(unreachable);
            return;
        }

        var idToken = await backchannel.ExchangeCodeAsync(
            provider, endpoints, code, OidcSupport.CallbackUrl(config, HttpContext, provider), flow.CodeVerifier, ct);
        if (idToken is null)
        {
            await Fail(unreachable);
            return;
        }

        var keys = await backchannel.SigningKeysAsync(provider, endpoints, OidcIdToken.KeyIdOf(idToken), ct);
        var (identity, refusal) = await OidcIdToken.ValidateAsync(idToken, provider, keys, flow.Nonce);
        if (identity is null)
        {
            // The reason is one of a fixed set of names. Nothing from the token is logged.
            logger.LogWarning("OIDC provider {Provider}: the id token was refused ({Refusal})", provider.Name, refusal);
            await Fail($"{provider.DisplayName} sign-in could not be verified. Please try again, or use your email code.");
            return;
        }

        var mfa = Resolve<barakoCMS.Infrastructure.Auth.Mfa.IMfaService>();
        SocialSignIn.Tokens tokens;
        bool emailNotVerified;
        try
        {
            (tokens, emailNotVerified) = await SocialSignIn.IssueForIdentityAsync(
                session, config, deviceGate, tokenIssuer, mfa, HttpContext, identity, provider.Name, club, ct);
        }
        catch (Exception ex) when (OidcSupport.IsUniqueViolation(ex))
        {
            // Two first sign-ins for one address at once: the unique index on the user's email let
            // one through. The loser is told to try again, and then finds the account the winner made.
            await Fail(expired);
            return;
        }

        if (emailNotVerified)
        {
            await Fail($"{provider.DisplayName} didn't share a verified email address. Please sign in with your email code instead.");
            return;
        }
        if (tokens.RequiresMfa)
        {
            await Send.ResultAsync(Results.Redirect(SocialSignIn.FrontendMfaCallback(baseUrl, tokens.MfaChallenge!, club)));
            return;
        }
        if (!tokens.Allowed)
        {
            await Fail("You are not a member of this club.");
            return;
        }
        await Send.ResultAsync(Results.Redirect(SocialSignIn.FrontendCallback(baseUrl, tokens.Token, tokens.Refresh, club)));
    }
}
