using System.Text.Json;
using FastEndpoints;
using Marten;
using Microsoft.AspNetCore.Http;

namespace BarakoCMS.ExternalAuth;

// "Continue with LinkedIn" — Sign In with LinkedIn using OpenID Connect. LinkedIn returns the
// person's verified email + name from /userinfo, which we hand to the shared SocialSignIn flow.
internal static class Li
{
    public const string Authorize = "https://www.linkedin.com/oauth/v2/authorization";
    public const string Token = "https://www.linkedin.com/oauth/v2/accessToken";
    public const string UserInfo = "https://api.linkedin.com/v2/userinfo";
    public const string Scope = "openid profile email";

    public static string CallbackUrl(IConfiguration c, HttpContext ctx) =>
        ExternalAuthSupport.BaseUrl(c, ctx) + "/api/auth/linkedin/callback";
}

/// <summary>GET /api/auth/linkedin/start?club={handle} — redirect to LinkedIn's consent dialog.</summary>
public class LinkedInStartEndpoint(IConfiguration config, IQuerySession session) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/api/auth/linkedin/start");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!ExternalAuthSupport.ProviderEnabled(config, "LinkedIn", "ClientId")) { await Send.NotFoundAsync(ct); return; }
        var club = (Query<string>("club", isRequired: false) ?? "").Trim().ToLowerInvariant();
        if (!await ExternalProviders.AllowedAsync(session, club, ExternalProviders.LinkedIn, ct)) { await Send.NotFoundAsync(ct); return; }
        var state = ExternalAuthSupport.NewState();

        HttpContext.Response.Cookies.Append("li_state", state, ExternalAuthSupport.ShortCookie());
        HttpContext.Response.Cookies.Append("li_club", club, ExternalAuthSupport.ShortCookie());

        var redirect = Li.CallbackUrl(config, HttpContext);
        var url =
            $"{Li.Authorize}?response_type=code&client_id={config["LinkedIn:ClientId"]}" +
            $"&redirect_uri={Uri.EscapeDataString(redirect)}" +
            $"&state={state}&scope={Uri.EscapeDataString(Li.Scope)}";
        await Send.ResultAsync(Results.Redirect(url));
    }
}

/// <summary>GET /api/auth/linkedin/callback — exchange the code, resolve the user, mint our token.</summary>
public class LinkedInCallbackEndpoint(
    IHttpClientFactory httpFactory,
    IDocumentSession session,
    IConfiguration config,
    barakoCMS.Core.Interfaces.IDeviceGate deviceGate,
    barakoCMS.Infrastructure.Auth.ITokenIssuer tokenIssuer) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/api/auth/linkedin/callback");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var baseUrl = ExternalAuthSupport.BaseUrl(config, HttpContext);
        var club = HttpContext.Request.Cookies["li_club"] ?? "";
        if (!await ExternalProviders.AllowedAsync(session, club, ExternalProviders.LinkedIn, ct)) { await Send.NotFoundAsync(ct); return; }
        var cookieState = HttpContext.Request.Cookies["li_state"];
        var code = Query<string>("code", isRequired: false);
        var state = Query<string>("state", isRequired: false);

        HttpContext.Response.Cookies.Delete("li_state");
        HttpContext.Response.Cookies.Delete("li_club");

        Task Fail(string message) =>
            Send.ResultAsync(Results.Redirect(SocialSignIn.FrontendError(baseUrl, message, club)));

        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state) || state != cookieState)
        {
            await Fail("LinkedIn sign-in was cancelled or the link expired. Please try again.");
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
                ["redirect_uri"] = Li.CallbackUrl(config, HttpContext),
                ["client_id"] = config["LinkedIn:ClientId"] ?? "",
                ["client_secret"] = config["LinkedIn:ClientSecret"] ?? "",
            });
            var tokenResp = await http.PostAsync(Li.Token, form, ct);
            tokenResp.EnsureSuccessStatusCode();
            var tokenDoc = JsonSerializer.Deserialize<JsonElement>(await tokenResp.Content.ReadAsStringAsync(ct));
            var accessToken = tokenDoc.GetProperty("access_token").GetString();

            using var infoReq = new HttpRequestMessage(System.Net.Http.HttpMethod.Get, Li.UserInfo);
            infoReq.Headers.Authorization = new("Bearer", accessToken);
            var infoResp = await http.SendAsync(infoReq, ct);
            infoResp.EnsureSuccessStatusCode();
            var me = JsonSerializer.Deserialize<JsonElement>(await infoResp.Content.ReadAsStringAsync(ct));

            subject = me.TryGetProperty("sub", out var sb) && sb.ValueKind == JsonValueKind.String ? sb.GetString() : null;
            email = (me.TryGetProperty("email", out var e) ? e.GetString() : null)?.Trim().ToLowerInvariant() ?? "";
            emailVerified = me.TryGetProperty("email_verified", out var ev)
                && (ev.ValueKind == System.Text.Json.JsonValueKind.True
                    || (ev.ValueKind == System.Text.Json.JsonValueKind.String && ev.GetString() == "true"));
            var name = me.TryGetProperty("name", out var n) ? n.GetString() : null;
            var photo = me.TryGetProperty("picture", out var p) ? p.GetString() : null;
            profile = new SocialSignIn.ProfileData(name, photo, null, null, "linkedin");
        }
        catch
        {
            await Fail("Couldn't reach LinkedIn. Please try again, or use your email code.");
            return;
        }

        var mfa = Resolve<barakoCMS.Infrastructure.Auth.Mfa.IMfaService>();
        await Send.ResultAsync(Results.Redirect(await SocialSignIn.CallbackRedirectAsync(
            session, config, deviceGate, tokenIssuer, mfa, HttpContext, ExternalProviders.LinkedIn, "LinkedIn",
            "LinkedIn didn't share an email address. Please sign in with your email code instead.",
            subject, email, emailVerified, club, baseUrl, ct, profile, claims)));
    }
}
