using System.Security.Cryptography;
using System.Text.Json;
using FastEndpoints;
using FastEndpoints.Security;
using Marten;
using Microsoft.AspNetCore.Http;
using barakoCMS.Infrastructure;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace BarakoCMS.ExternalAuth;

// "Continue with Facebook" sign-in. Facebook is used only to prove the person's email — our identity
// anchor. We match the email to an existing user (created up-front by a club officer, or from a prior
// sign-in) or create a global account, then mint the same tenant-scoped, device-bound token the email
// and password flows issue. Uses only email + public_profile, so no Facebook App Review is required.
internal static class Fb
{
    public const string Graph = "https://graph.facebook.com/v21.0";
    public const string Dialog = "https://www.facebook.com/v21.0/dialog/oauth";

    public static string CallbackUrl(IConfiguration config, HttpContext ctx) =>
        ExternalAuthSupport.BaseUrl(config, ctx) + "/api/auth/facebook/callback";
}

/// <summary>GET /api/auth/facebook/start?club={handle} — redirect the browser to Facebook's consent dialog.</summary>
public class FacebookStartEndpoint : EndpointWithoutRequest
{
    private readonly IConfiguration _config;
    public FacebookStartEndpoint(IConfiguration config) => _config = config;

    public override void Configure()
    {
        Get("/api/auth/facebook/start");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!ExternalAuthSupport.ProviderEnabled(_config, "Facebook", "AppId")) { await SendNotFoundAsync(ct); return; }
        var club = (Query<string>("club", isRequired: false) ?? "").Trim().ToLowerInvariant();
        var state = Guid.NewGuid().ToString("N");

        HttpContext.Response.Cookies.Append("fb_state", state, ExternalAuthSupport.ShortCookie());
        HttpContext.Response.Cookies.Append("fb_club", club, ExternalAuthSupport.ShortCookie());

        var appId = _config["Facebook:AppId"];
        var redirect = Fb.CallbackUrl(_config, HttpContext);
        var url =
            $"{Fb.Dialog}?client_id={appId}" +
            $"&redirect_uri={Uri.EscapeDataString(redirect)}" +
            $"&state={state}&response_type=code&scope=email,public_profile,user_birthday,user_location";

        await SendResultAsync(Results.Redirect(url));
    }
}

/// <summary>GET /api/auth/facebook/callback — exchange the code, resolve the user, mint our token, hand it back.</summary>
public class FacebookCallbackEndpoint : EndpointWithoutRequest
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly IDocumentSession _session;
    private readonly IConfiguration _config;
    private readonly barakoCMS.Core.Interfaces.IDeviceGate _deviceGate;
    private readonly barakoCMS.Infrastructure.Auth.ITokenIssuer _tokenIssuer;

    public FacebookCallbackEndpoint(
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
        Get("/api/auth/facebook/callback");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var baseUrl = ExternalAuthSupport.BaseUrl(_config, HttpContext);
        var club = HttpContext.Request.Cookies["fb_club"] ?? "";
        var cookieState = HttpContext.Request.Cookies["fb_state"];
        var code = Query<string>("code", isRequired: false);
        var state = Query<string>("state", isRequired: false);

        HttpContext.Response.Cookies.Delete("fb_state");
        HttpContext.Response.Cookies.Delete("fb_club");

        async Task Fail(string message)
        {
            var to = $"{baseUrl}/login?fberror={Uri.EscapeDataString(message)}";
            if (!string.IsNullOrEmpty(club)) to += $"&club={Uri.EscapeDataString(club)}";
            await SendResultAsync(Results.Redirect(to));
        }

        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state) || state != cookieState)
        {
            await Fail("Facebook sign-in was cancelled or the link expired. Please try again.");
            return;
        }

        string email;
        SocialSignIn.ProfileData profile;
        try
        {
            var http = _httpFactory.CreateClient();
            var redirect = Fb.CallbackUrl(_config, HttpContext);

            var tokenUrl =
                $"{Fb.Graph}/oauth/access_token?client_id={_config["Facebook:AppId"]}" +
                $"&redirect_uri={Uri.EscapeDataString(redirect)}" +
                $"&client_secret={_config["Facebook:AppSecret"]}" +
                $"&code={Uri.EscapeDataString(code)}";
            var tokenDoc = await http.GetFromJsonAsync<JsonElement>(tokenUrl, ct);
            var userToken = tokenDoc.GetProperty("access_token").GetString();

            // birthday/location arrive only when the user granted user_birthday / user_location.
            var meUrl = $"{Fb.Graph}/me?fields=id,name,email,picture.type(large),birthday,location" +
                        $"&access_token={Uri.EscapeDataString(userToken!)}";
            var me = await http.GetFromJsonAsync<JsonElement>(meUrl, ct);

            email = (me.TryGetProperty("email", out var e) ? e.GetString() : null)?.Trim().ToLowerInvariant() ?? "";
            var name = me.TryGetProperty("name", out var n) ? n.GetString() : null;
            var photo = me.TryGetProperty("picture", out var pic) && pic.TryGetProperty("data", out var pd)
                && pd.TryGetProperty("url", out var pu) ? pu.GetString() : null;
            var birthday = me.TryGetProperty("birthday", out var b) ? b.GetString() : null;
            var location = me.TryGetProperty("location", out var loc) && loc.TryGetProperty("name", out var ln)
                ? ln.GetString() : null;
            profile = new SocialSignIn.ProfileData(name, photo, birthday, location, "facebook");
        }
        catch
        {
            await Fail("Couldn't reach Facebook. Please try again, or use your email code.");
            return;
        }

        if (string.IsNullOrEmpty(email) || !email.Contains('@'))
        {
            await Fail("Facebook didn't share an email address. Please sign in with your email code instead.");
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
