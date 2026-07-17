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

    public static string CallbackUrl(IConfiguration c, HttpContext ctx) =>
        ExternalAuthSupport.BaseUrl(c, ctx) + "/api/auth/google/callback";
}

/// <summary>GET /api/auth/google/start?club={handle}</summary>
public class GoogleStartEndpoint : EndpointWithoutRequest
{
    private readonly IConfiguration _config;
    public GoogleStartEndpoint(IConfiguration config) => _config = config;

    public override void Configure()
    {
        Get("/api/auth/google/start");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!ExternalAuthSupport.ProviderEnabled(_config, "Google", "ClientId")) { await SendNotFoundAsync(ct); return; }
        var club = (Query<string>("club", isRequired: false) ?? "").Trim().ToLowerInvariant();
        var state = Guid.NewGuid().ToString("N");
        HttpContext.Response.Cookies.Append("gg_state", state, ExternalAuthSupport.ShortCookie());
        HttpContext.Response.Cookies.Append("gg_club", club, ExternalAuthSupport.ShortCookie());

        var redirect = Gg.CallbackUrl(_config, HttpContext);
        var url =
            $"{Gg.Authorize}?response_type=code&client_id={_config["Google:ClientId"]}" +
            $"&redirect_uri={Uri.EscapeDataString(redirect)}" +
            $"&state={state}&scope={Uri.EscapeDataString(Gg.Scope)}";
        await SendResultAsync(Results.Redirect(url));
    }
}

/// <summary>GET /api/auth/google/callback</summary>
public class GoogleCallbackEndpoint : EndpointWithoutRequest
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly IDocumentSession _session;
    private readonly IConfiguration _config;
    private readonly barakoCMS.Core.Interfaces.IDeviceGate _deviceGate;

    public GoogleCallbackEndpoint(IHttpClientFactory httpFactory, IDocumentSession session,
        IConfiguration config, barakoCMS.Core.Interfaces.IDeviceGate deviceGate)
    {
        _httpFactory = httpFactory;
        _session = session;
        _config = config;
        _deviceGate = deviceGate;
    }

    public override void Configure()
    {
        Get("/api/auth/google/callback");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var baseUrl = ExternalAuthSupport.BaseUrl(_config, HttpContext);
        var club = HttpContext.Request.Cookies["gg_club"] ?? "";
        var cookieState = HttpContext.Request.Cookies["gg_state"];
        var code = Query<string>("code", isRequired: false);
        var state = Query<string>("state", isRequired: false);
        HttpContext.Response.Cookies.Delete("gg_state");
        HttpContext.Response.Cookies.Delete("gg_club");

        async Task Fail(string message)
        {
            var to = $"{baseUrl}/login?fberror={Uri.EscapeDataString(message)}";
            if (!string.IsNullOrEmpty(club)) to += $"&club={Uri.EscapeDataString(club)}";
            await SendResultAsync(Results.Redirect(to));
        }

        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state) || state != cookieState)
        {
            await Fail("Google sign-in was cancelled or the link expired. Please try again.");
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
                ["redirect_uri"] = Gg.CallbackUrl(_config, HttpContext),
                ["client_id"] = _config["Google:ClientId"] ?? "",
                ["client_secret"] = _config["Google:ClientSecret"] ?? "",
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

            email = (me.TryGetProperty("email", out var e) ? e.GetString() : null)?.Trim().ToLowerInvariant() ?? "";
            var name = me.TryGetProperty("name", out var n) ? n.GetString() : null;
            var photo = me.TryGetProperty("picture", out var p) ? p.GetString() : null;
            profile = new SocialSignIn.ProfileData(name, photo, null, null, "google");
        }
        catch
        {
            await Fail("Couldn't reach Google. Please try again, or use your email code.");
            return;
        }

        if (string.IsNullOrEmpty(email) || !email.Contains('@'))
        {
            await Fail("Google didn't share an email address. Please sign in with your email code instead.");
            return;
        }

        var tokens = await SocialSignIn.IssueAsync(_session, _config, _deviceGate, HttpContext, email, club, ct, profile);
        await SendResultAsync(Results.Redirect(SocialSignIn.FrontendCallback(baseUrl, tokens.Token, tokens.Refresh, club)));
    }
}
