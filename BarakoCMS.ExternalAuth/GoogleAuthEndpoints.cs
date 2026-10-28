using System.Text.Json;
using FastEndpoints;
using Marten;
using Microsoft.AspNetCore.Http;

namespace BarakoCMS.ExternalAuth;

// "Continue with Google" — Google OpenID Connect. Returns verified email + name + picture.
internal static class Gg
{
    public const string Authorize = "https://accounts.google.com/o/oauth2/v2/auth";
    public const string Token = "https://oauth2.googleapis.com/token";
    public const string UserInfo = "https://openidconnect.googleapis.com/v1/userinfo";
    public const string Scope = "openid email profile";

    /// <summary>
    /// The claims of the id_token Google returned from its token endpoint, for the role map. A token
    /// received straight from Google over HTTPS needs no signature check, but it must be for this
    /// client, from Google, unexpired, and for the same person userinfo named. Otherwise none.
    /// </summary>
    internal static IReadOnlyDictionary<string, IReadOnlyList<string>>? IdTokenClaims(
        JsonElement tokenResponse, string? clientId, string? subject)
    {
        try
        {
            if (!tokenResponse.TryGetProperty("id_token", out var raw) || raw.ValueKind != JsonValueKind.String
                || string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(subject))
            {
                return null;
            }

            var parts = raw.GetString()!.Split('.');
            if (parts.Length != 3)
            {
                return null;
            }

            using var doc = JsonDocument.Parse(Microsoft.IdentityModel.Tokens.Base64UrlEncoder.DecodeBytes(parts[1]));
            var payload = doc.RootElement;
            var iss = payload.TryGetProperty("iss", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() : null;
            var aud = payload.TryGetProperty("aud", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
            var sub = payload.TryGetProperty("sub", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
            var exp = payload.TryGetProperty("exp", out var e) && e.ValueKind == JsonValueKind.Number ? e.GetInt64() : 0;
            if ((iss != "accounts.google.com" && iss != "https://accounts.google.com")
                || aud != clientId || sub != subject || exp <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            {
                return null;
            }

            return OidcIdToken.StringClaims(payload);
        }
        catch
        {
            return null;
        }
    }

    public static string CallbackUrl(IConfiguration c, HttpContext ctx) =>
        ExternalAuthSupport.BaseUrl(c, ctx) + "/api/auth/google/callback";
}

/// <summary>GET /api/auth/google/start?club={handle}</summary>
public class GoogleStartEndpoint(IConfiguration config, IQuerySession session) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/api/auth/google/start");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!ExternalAuthSupport.ProviderEnabled(config, "Google", "ClientId")) { await Send.NotFoundAsync(ct); return; }
        var club = (Query<string>("club", isRequired: false) ?? "").Trim().ToLowerInvariant();
        if (!await ExternalProviders.AllowedAsync(session, club, ExternalProviders.Google, ct)) { await Send.NotFoundAsync(ct); return; }
        var state = ExternalAuthSupport.NewState();
        HttpContext.Response.Cookies.Append("gg_state", state, ExternalAuthSupport.ShortCookie());
        HttpContext.Response.Cookies.Append("gg_club", club, ExternalAuthSupport.ShortCookie());

        var redirect = Gg.CallbackUrl(config, HttpContext);
        var url =
            $"{Gg.Authorize}?response_type=code&client_id={config["Google:ClientId"]}" +
            $"&redirect_uri={Uri.EscapeDataString(redirect)}" +
            $"&state={state}&scope={Uri.EscapeDataString(Gg.Scope)}";
        await Send.ResultAsync(Results.Redirect(url));
    }
}

/// <summary>GET /api/auth/google/callback</summary>
public class GoogleCallbackEndpoint(
    IHttpClientFactory httpFactory,
    IDocumentSession session,
    IConfiguration config,
    barakoCMS.Core.Interfaces.IDeviceGate deviceGate,
    barakoCMS.Infrastructure.Auth.ITokenIssuer tokenIssuer) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/api/auth/google/callback");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var baseUrl = ExternalAuthSupport.BaseUrl(config, HttpContext);
        var club = HttpContext.Request.Cookies["gg_club"] ?? "";
        if (!await ExternalProviders.AllowedAsync(session, club, ExternalProviders.Google, ct)) { await Send.NotFoundAsync(ct); return; }
        var cookieState = HttpContext.Request.Cookies["gg_state"];
        var code = Query<string>("code", isRequired: false);
        var state = Query<string>("state", isRequired: false);
        HttpContext.Response.Cookies.Delete("gg_state");
        HttpContext.Response.Cookies.Delete("gg_club");

        Task Fail(string message) =>
            Send.ResultAsync(Results.Redirect(SocialSignIn.FrontendError(baseUrl, message, club)));

        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state) || state != cookieState)
        {
            await Fail("Google sign-in was cancelled or the link expired. Please try again.");
            return;
        }

        string email;
        bool emailVerified;
        string? subject;
        IReadOnlyDictionary<string, IReadOnlyList<string>>? claims = null;
        SocialSignIn.ProfileData profile;
        try
        {
            var http = httpFactory.CreateClient();
            var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = Gg.CallbackUrl(config, HttpContext),
                ["client_id"] = config["Google:ClientId"] ?? "",
                ["client_secret"] = config["Google:ClientSecret"] ?? "",
            });
            var tokenResp = await http.PostAsync(Gg.Token, form, ct);
            tokenResp.EnsureSuccessStatusCode();
            var tokenDoc = JsonSerializer.Deserialize<JsonElement>(await tokenResp.Content.ReadAsStringAsync(ct));
            var accessToken = tokenDoc.GetProperty("access_token").GetString();

            using var infoReq = new HttpRequestMessage(System.Net.Http.HttpMethod.Get, Gg.UserInfo);
            infoReq.Headers.Authorization = new("Bearer", accessToken);
            var infoResp = await http.SendAsync(infoReq, ct);
            infoResp.EnsureSuccessStatusCode();
            var me = JsonSerializer.Deserialize<JsonElement>(await infoResp.Content.ReadAsStringAsync(ct));

            subject = me.TryGetProperty("sub", out var sb) && sb.ValueKind == JsonValueKind.String ? sb.GetString() : null;
            // hd is an ID token claim only, so it is read from the id_token of the code exchange.
            claims = Gg.IdTokenClaims(tokenDoc, config["Google:ClientId"], subject);
            email = (me.TryGetProperty("email", out var e) ? e.GetString() : null)?.Trim().ToLowerInvariant() ?? "";
            // OIDC: email_verified is the provider's assertion that it owns the verification, and
            // absent means no assertion, so absent is false.
            emailVerified = me.TryGetProperty("email_verified", out var ev)
                && (ev.ValueKind == System.Text.Json.JsonValueKind.True
                    || (ev.ValueKind == System.Text.Json.JsonValueKind.String && ev.GetString() == "true"));
            var name = me.TryGetProperty("name", out var n) ? n.GetString() : null;
            var photo = me.TryGetProperty("picture", out var p) ? p.GetString() : null;
            profile = new SocialSignIn.ProfileData(name, photo, null, null, "google");
        }
        catch
        {
            await Fail("Couldn't reach Google. Please try again, or use your email code.");
            return;
        }

        var mfa = Resolve<barakoCMS.Infrastructure.Auth.Mfa.IMfaService>();
        await Send.ResultAsync(Results.Redirect(await SocialSignIn.CallbackRedirectAsync(
            session, config, deviceGate, tokenIssuer, mfa, HttpContext, ExternalProviders.Google, "Google",
            "Google didn't share an email address. Please sign in with your email code instead.",
            subject, email, emailVerified, club, baseUrl, ct, profile, claims)));
    }
}
