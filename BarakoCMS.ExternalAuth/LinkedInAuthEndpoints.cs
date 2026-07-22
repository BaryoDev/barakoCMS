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
public class LinkedInStartEndpoint : EndpointWithoutRequest
{
    private readonly IConfiguration _config;
    public LinkedInStartEndpoint(IConfiguration config) => _config = config;

    public override void Configure()
    {
        Get("/api/auth/linkedin/start");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!ExternalAuthSupport.ProviderEnabled(_config, "LinkedIn", "ClientId")) { await SendNotFoundAsync(ct); return; }
        var club = (Query<string>("club", isRequired: false) ?? "").Trim().ToLowerInvariant();
        var state = Guid.NewGuid().ToString("N");

        HttpContext.Response.Cookies.Append("li_state", state, ExternalAuthSupport.ShortCookie());
        HttpContext.Response.Cookies.Append("li_club", club, ExternalAuthSupport.ShortCookie());

        var redirect = Li.CallbackUrl(_config, HttpContext);
        var url =
            $"{Li.Authorize}?response_type=code&client_id={_config["LinkedIn:ClientId"]}" +
            $"&redirect_uri={Uri.EscapeDataString(redirect)}" +
            $"&state={state}&scope={Uri.EscapeDataString(Li.Scope)}";
        await SendResultAsync(Results.Redirect(url));
    }
}

/// <summary>GET /api/auth/linkedin/callback — exchange the code, resolve the user, mint our token.</summary>
public class LinkedInCallbackEndpoint : EndpointWithoutRequest
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly IDocumentSession _session;
    private readonly IConfiguration _config;
    private readonly barakoCMS.Core.Interfaces.IDeviceGate _deviceGate;
    private readonly barakoCMS.Infrastructure.Auth.ITokenIssuer _tokenIssuer;

    public LinkedInCallbackEndpoint(
        IHttpClientFactory httpFactory,
        IDocumentSession session,
        IConfiguration config,
        barakoCMS.Core.Interfaces.IDeviceGate deviceGate,
        barakoCMS.Infrastructure.Auth.ITokenIssuer tokenIssuer)
    {
        _httpFactory = httpFactory;
        _session = session;
        _config = config;
        _deviceGate = deviceGate;
        _tokenIssuer = tokenIssuer;
    }

    public override void Configure()
    {
        Get("/api/auth/linkedin/callback");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var baseUrl = ExternalAuthSupport.BaseUrl(_config, HttpContext);
        var club = HttpContext.Request.Cookies["li_club"] ?? "";
        var cookieState = HttpContext.Request.Cookies["li_state"];
        var code = Query<string>("code", isRequired: false);
        var state = Query<string>("state", isRequired: false);

        HttpContext.Response.Cookies.Delete("li_state");
        HttpContext.Response.Cookies.Delete("li_club");

        async Task Fail(string message)
        {
            var to = $"{baseUrl}/login?fberror={Uri.EscapeDataString(message)}";
            if (!string.IsNullOrEmpty(club)) to += $"&club={Uri.EscapeDataString(club)}";
            await SendResultAsync(Results.Redirect(to));
        }

        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state) || state != cookieState)
        {
            await Fail("LinkedIn sign-in was cancelled or the link expired. Please try again.");
            return;
        }

        string email;
        SocialSignIn.ProfileData profile;
        try
        {
            var http = _httpFactory.CreateClient();

            var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = Li.CallbackUrl(_config, HttpContext),
                ["client_id"] = _config["LinkedIn:ClientId"] ?? "",
                ["client_secret"] = _config["LinkedIn:ClientSecret"] ?? "",
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

            email = (me.TryGetProperty("email", out var e) ? e.GetString() : null)?.Trim().ToLowerInvariant() ?? "";
            var name = me.TryGetProperty("name", out var n) ? n.GetString() : null;
            var photo = me.TryGetProperty("picture", out var p) ? p.GetString() : null;
            profile = new SocialSignIn.ProfileData(name, photo, null, null, "linkedin");
        }
        catch
        {
            await Fail("Couldn't reach LinkedIn. Please try again, or use your email code.");
            return;
        }

        if (string.IsNullOrEmpty(email) || !email.Contains('@'))
        {
            await Fail("LinkedIn didn't share an email address. Please sign in with your email code instead.");
            return;
        }

        var tokens = await SocialSignIn.IssueAsync(_session, _config, _deviceGate, _tokenIssuer, HttpContext, email, club, ct, profile);
        if (!tokens.Allowed)
        {
            await Fail("You are not a member of this club.");
            return;
        }
        await SendResultAsync(Results.Redirect(SocialSignIn.FrontendCallback(baseUrl, tokens.Token, tokens.Refresh, club)));
    }
}
