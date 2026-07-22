using System.Text.Json;
using FastEndpoints;
using Marten;
using Microsoft.AspNetCore.Http;

namespace BarakoCMS.ExternalAuth;

// "Continue with GitHub". GitHub's user endpoint may hide the email (if private), so we also read
// /user/emails and pick the primary verified one.
internal static class Gh
{
    public const string Authorize = "https://github.com/login/oauth/authorize";
    public const string Token = "https://github.com/login/oauth/access_token";
    public const string User = "https://api.github.com/user";
    public const string Emails = "https://api.github.com/user/emails";
    public const string Scope = "read:user user:email";

    public static string CallbackUrl(IConfiguration c, HttpContext ctx) =>
        ExternalAuthSupport.BaseUrl(c, ctx) + "/api/auth/github/callback";
}

/// <summary>GET /api/auth/github/start?club={handle}</summary>
public class GitHubStartEndpoint : EndpointWithoutRequest
{
    private readonly IConfiguration _config;
    public GitHubStartEndpoint(IConfiguration config) => _config = config;

    public override void Configure()
    {
        Get("/api/auth/github/start");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!ExternalAuthSupport.ProviderEnabled(_config, "GitHub", "ClientId")) { await SendNotFoundAsync(ct); return; }
        var club = (Query<string>("club", isRequired: false) ?? "").Trim().ToLowerInvariant();
        var state = Guid.NewGuid().ToString("N");
        HttpContext.Response.Cookies.Append("gh_state", state, ExternalAuthSupport.ShortCookie());
        HttpContext.Response.Cookies.Append("gh_club", club, ExternalAuthSupport.ShortCookie());

        var redirect = Gh.CallbackUrl(_config, HttpContext);
        var url =
            $"{Gh.Authorize}?client_id={_config["GitHub:ClientId"]}" +
            $"&redirect_uri={Uri.EscapeDataString(redirect)}" +
            $"&state={state}&scope={Uri.EscapeDataString(Gh.Scope)}";
        await SendResultAsync(Results.Redirect(url));
    }
}

/// <summary>GET /api/auth/github/callback</summary>
public class GitHubCallbackEndpoint : EndpointWithoutRequest
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly IDocumentSession _session;
    private readonly IConfiguration _config;
    private readonly barakoCMS.Core.Interfaces.IDeviceGate _deviceGate;
    private readonly barakoCMS.Infrastructure.Auth.ITokenIssuer _tokenIssuer;

    public GitHubCallbackEndpoint(IHttpClientFactory httpFactory, IDocumentSession session,
        IConfiguration config, barakoCMS.Core.Interfaces.IDeviceGate deviceGate,
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
        Get("/api/auth/github/callback");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var baseUrl = ExternalAuthSupport.BaseUrl(_config, HttpContext);
        var club = HttpContext.Request.Cookies["gh_club"] ?? "";
        var cookieState = HttpContext.Request.Cookies["gh_state"];
        var code = Query<string>("code", isRequired: false);
        var state = Query<string>("state", isRequired: false);
        HttpContext.Response.Cookies.Delete("gh_state");
        HttpContext.Response.Cookies.Delete("gh_club");

        async Task Fail(string message)
        {
            var to = $"{baseUrl}/login?fberror={Uri.EscapeDataString(message)}";
            if (!string.IsNullOrEmpty(club)) to += $"&club={Uri.EscapeDataString(club)}";
            await SendResultAsync(Results.Redirect(to));
        }

        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state) || state != cookieState)
        {
            await Fail("GitHub sign-in was cancelled or the link expired. Please try again.");
            return;
        }

        string email;
        SocialSignIn.ProfileData profile;
        try
        {
            var http = _httpFactory.CreateClient();

            using var tokenReq = new HttpRequestMessage(System.Net.Http.HttpMethod.Post, Gh.Token)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = _config["GitHub:ClientId"] ?? "",
                    ["client_secret"] = _config["GitHub:ClientSecret"] ?? "",
                    ["code"] = code,
                    ["redirect_uri"] = Gh.CallbackUrl(_config, HttpContext),
                }),
            };
            tokenReq.Headers.Accept.Add(new("application/json"));
            var tokenResp = await http.SendAsync(tokenReq, ct);
            tokenResp.EnsureSuccessStatusCode();
            var tokenDoc = JsonSerializer.Deserialize<JsonElement>(await tokenResp.Content.ReadAsStringAsync(ct));
            var accessToken = tokenDoc.GetProperty("access_token").GetString();

            var me = await GhGet(http, Gh.User, accessToken!, ct);
            email = (me.TryGetProperty("email", out var e) ? e.GetString() : null)?.Trim().ToLowerInvariant() ?? "";
            var name = me.TryGetProperty("name", out var n) ? n.GetString() : null;
            var photo = me.TryGetProperty("avatar_url", out var a) ? a.GetString() : null;
            var location = me.TryGetProperty("location", out var l) ? l.GetString() : null;

            if (string.IsNullOrEmpty(email))
            {
                // Email is private on the profile — read the verified primary from /user/emails.
                var emails = await GhGet(http, Gh.Emails, accessToken!, ct);
                foreach (var item in emails.EnumerateArray())
                {
                    var verified = item.TryGetProperty("verified", out var v) && v.GetBoolean();
                    var primary = item.TryGetProperty("primary", out var pr) && pr.GetBoolean();
                    if (verified && primary && item.TryGetProperty("email", out var em))
                    {
                        email = (em.GetString() ?? "").Trim().ToLowerInvariant();
                        break;
                    }
                }
            }
            profile = new SocialSignIn.ProfileData(name, photo, null, location, "github");
        }
        catch
        {
            await Fail("Couldn't reach GitHub. Please try again, or use your email code.");
            return;
        }

        if (string.IsNullOrEmpty(email) || !email.Contains('@'))
        {
            await Fail("GitHub didn't share a verified email address. Please sign in with your email code instead.");
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

    private static async Task<JsonElement> GhGet(HttpClient http, string url, string token, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(System.Net.Http.HttpMethod.Get, url);
        req.Headers.Authorization = new("Bearer", token);
        req.Headers.Accept.Add(new("application/vnd.github+json"));
        req.Headers.UserAgent.Add(new("BaryoClub", "1.0")); // GitHub requires a User-Agent
        var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync(ct));
    }
}
