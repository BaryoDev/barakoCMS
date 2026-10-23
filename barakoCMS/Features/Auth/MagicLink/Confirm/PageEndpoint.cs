using System.Net;
using FastEndpoints;

namespace barakoCMS.Features.Auth.MagicLink.Confirm;

/// <summary>
/// GET /api/auth/magic-link/confirm: the page the emailed link opens. It shows a confirm button and
/// changes nothing. Mail scanners and link previews fetch links with GET, so a GET that signed
/// somebody in, or spent the link, would let them do it for the user.
/// </summary>
internal class PageEndpoint : Endpoint<MagicLinkConfirmRequest>
{
    public override void Configure()
    {
        Get("/api/auth/magic-link/confirm");
        AllowAnonymous();
        Options(x => x.RequireRateLimiting("auth"));
    }

    public override async Task HandleAsync(MagicLinkConfirmRequest req, CancellationToken ct)
    {
        var token = WebUtility.HtmlEncode(req.Token ?? string.Empty);
        var html =
            "<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"referrer\" content=\"no-referrer\">"
          + "<title>Sign in</title></head><body>"
          + "<form method=\"post\" action=\"/api/auth/magic-link/confirm\">"
          + $"<input type=\"hidden\" name=\"token\" value=\"{token}\">"
          + "<p>Confirm that you want to sign in.</p>"
          + "<button type=\"submit\">Sign in</button></form></body></html>";

        HttpContext.Response.Headers.CacheControl = "no-store";
        HttpContext.Response.Headers["Referrer-Policy"] = "no-referrer";
        await Send.StringAsync(html, 200, "text/html", ct);
    }
}
