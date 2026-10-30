using System.Net;
using System.Text.Json.Serialization;
using FastEndpoints;
using FluentValidation;
using Marten;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;

namespace BarakoCMS.ExternalAuth;

internal sealed class FacebookDeletionStatusResponse
{
    public string ConfirmationCode { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public DateTime RequestedAt { get; set; }

    /// <summary>When an unanswered choice erases the account. Only while the status is awaiting_choice.</summary>
    public DateTime? ChoiceExpiresAt { get; set; }
}

internal static class FacebookDeletionPage
{
    public const string DeleteAccount = "delete_account";
    public const string KeepAccount = "keep_account";

    /// <summary>The code in a URL is a hex string. Anything else is not one of ours.</summary>
    public static bool LooksLikeCode(string code) =>
        code.Length is >= 16 and <= 128 && code.All(char.IsAsciiHexDigit);

    public static bool WantsHtml(HttpRequest request) =>
        request.Headers.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase);

    public static bool TryParseChoice(string? choice, out DeletionChoice parsed)
    {
        parsed = choice switch
        {
            DeleteAccount => DeletionChoice.Delete,
            _ => DeletionChoice.Keep,
        };
        return choice is DeleteAccount or KeepAccount;
    }

    public static async Task SendPageAsync(HttpContext http, FacebookDeletionRequest request, string status, CancellationToken ct)
    {
        // The stored id is a hash, not the code, so the form posts to the URL the person already holds.
        var action = $"/api/auth/facebook/data-deletion/status/{WebUtility.HtmlEncode((string?)http.Request.RouteValues["code"] ?? "")}";
        var body = status switch
        {
            FacebookDeletionStatus.AwaitingChoice =>
                "<p>Facebook removed this site's access to your account. Facebook was your only way to sign in.</p>"
              + "<p>Choose what happens to your account here. If you do not choose, the account is deleted when the time runs out"
              + (request.ExpiresAt is { } due ? $" ({due:yyyy-MM-dd})" : "") + ".</p>"
              + $"<form method=\"post\" action=\"{action}\">"
              + "<button type=\"submit\" name=\"choice\" value=\"delete_account\">Delete my account</button> "
              + "<button type=\"submit\" name=\"choice\" value=\"keep_account\">Keep my account</button></form>",
            FacebookDeletionStatus.Kept => "<p>Your account was kept. Your Facebook link is removed.</p>",
            FacebookDeletionStatus.NeedsAdmin =>
                "<p>Your Facebook link is removed. Your account is an administrator account, so an administrator has to review it before it can be deleted.</p>",
            _ => "<p>Your Facebook link is removed, and your data deletion request is complete.</p>",
        };

        var html =
            "<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"referrer\" content=\"no-referrer\">"
          + "<title>Data deletion</title></head><body><h1>Data deletion</h1>" + body + "</body></html>";

        var response = http.Response;
        response.Headers.CacheControl = "no-store";
        response.Headers["Referrer-Policy"] = "no-referrer";
        // Overrides the host's default policy: this page needs a same-origin form post and nothing else.
        response.Headers["Content-Security-Policy"] =
            "default-src 'none'; form-action 'self'; base-uri 'none'; frame-ancestors 'none'";
        response.Headers.Vary = "Accept";
        response.StatusCode = 200;
        response.ContentType = "text/html; charset=utf-8";
        await response.WriteAsync(html, ct);
    }
}

/// <summary>
/// GET /api/auth/facebook/data-deletion/status/{code}. What became of one deletion request: JSON,
/// or a small page with the two choices when the client asks for HTML. It changes nothing, so a
/// link preview or scanner that fetches it cannot delete an account.
/// </summary>
internal sealed class FacebookDataDeletionStatusEndpoint(IQuerySession session)
    : EndpointWithoutRequest<FacebookDeletionStatusResponse>
{
    public override void Configure()
    {
        Get("/api/auth/facebook/data-deletion/status/{code}");
        AllowAnonymous();
        Options(x => x.RequireRateLimiting(SocialRateLimit.Policy));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var code = Route<string>("code") ?? "";
        var found = FacebookDeletionPage.LooksLikeCode(code)
            ? await session.LoadAsync<FacebookDeletionRequest>(FacebookDeletionFlow.HashOf(code), ct)
            : null;
        if (found is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        HttpContext.Response.Headers.Vary = "Accept";
        if (FacebookDeletionPage.WantsHtml(HttpContext.Request))
        {
            await FacebookDeletionPage.SendPageAsync(HttpContext, found, found.Status, ct);
            return;
        }

        await Send.OkAsync(new FacebookDeletionStatusResponse
        {
            ConfirmationCode = code,
            Status = found.Status,
            RequestedAt = found.RequestedAt,
            ChoiceExpiresAt = found.ExpiresAt,
        }, ct);
    }
}

internal sealed class FacebookDeletionChoiceRequest
{
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("choice")]
    public string? Choice { get; set; }
}

internal sealed class FacebookDeletionChoiceValidator : Validator<FacebookDeletionChoiceRequest>
{
    public FacebookDeletionChoiceValidator()
    {
        RuleFor(x => x.Choice)
            .Must(c => c is FacebookDeletionPage.DeleteAccount or FacebookDeletionPage.KeepAccount)
            .WithMessage("The choice must be delete_account or keep_account.");
    }
}

/// <summary>
/// POST /api/auth/facebook/data-deletion/status/{code}. The person's choice about an account whose
/// only login was Facebook: <c>delete_account</c> or <c>keep_account</c>, as JSON or a form field.
/// The code is the capability. A choice after the request settled changes nothing and answers with
/// the status it settled on, so of two racing choices the first save wins and the other reports it.
/// </summary>
internal sealed class FacebookDeletionChoiceEndpoint(IDocumentSession session, IDocumentStore store, UserEraser eraser)
    : Endpoint<FacebookDeletionChoiceRequest, FacebookDeletionStatusResponse>
{
    public override void Configure()
    {
        Post("/api/auth/facebook/data-deletion/status/{code}");
        AllowAnonymous();
        AllowFormData(urlEncoded: true);
        Options(x => x.RequireRateLimiting(SocialRateLimit.Policy));
    }

    public override async Task HandleAsync(FacebookDeletionChoiceRequest req, CancellationToken ct)
    {
        if (!FacebookDeletionPage.LooksLikeCode(req.Code))
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var id = FacebookDeletionFlow.HashOf(req.Code);
        var request = await session.LoadAsync<FacebookDeletionRequest>(id, ct);
        if (request is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        FacebookDeletionPage.TryParseChoice(req.Choice, out var choice);
        var wasOpen = request.Status == FacebookDeletionStatus.AwaitingChoice;
        string status;
        try
        {
            status = await FacebookDeletionFlow.SettleAsync(session, eraser, request, choice, DateTime.UtcNow, ct);
            if (wasOpen)
            {
                await session.SaveChangesAsync(ct);
            }
        }
        catch (JasperFx.ConcurrencyException)
        {
            await using var read = store.QuerySession();
            request = await read.LoadAsync<FacebookDeletionRequest>(id, ct) ?? request;
            status = request.Status;
        }

        HttpContext.Response.Headers.Vary = "Accept";
        if (FacebookDeletionPage.WantsHtml(HttpContext.Request))
        {
            await FacebookDeletionPage.SendPageAsync(HttpContext, request, status, ct);
            return;
        }

        await Send.OkAsync(new FacebookDeletionStatusResponse
        {
            ConfirmationCode = req.Code,
            Status = status,
            RequestedAt = request.RequestedAt,
            ChoiceExpiresAt = request.ExpiresAt,
        }, ct);
    }
}
