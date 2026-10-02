using barakoCMS.Models;
using FastEndpoints;
using FluentValidation;
using FluentValidation.Results;
using Marten;
// RequireRateLimiting lives here; this project is not a Web SDK project, so it is not implicitly used.
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Options;

namespace BarakoCMS.Forms.Features.EmailCode;

internal sealed class EmailCodeRequest
{
    /// <summary>The address the visitor will submit, which is where the code goes.</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>Left empty by a person, as on submit.</summary>
    public string? Honeypot { get; set; }

    /// <summary>The Cloudflare Turnstile token, required only when Turnstile is configured.</summary>
    public string? TurnstileToken { get; set; }
}

internal sealed class EmailCodeResponse
{
    public bool Accepted { get; set; } = true;
}

internal sealed class EmailCodeRequestValidator : Validator<EmailCodeRequest>
{
    public EmailCodeRequestValidator()
    {
        RuleFor(x => x.Email)
            .Must(address => FormEmailVerifier.IsAddress(address))
            .WithMessage("Email must be one email address.");
    }
}

/// <summary>
/// POST /api/public/forms/{slug}/email-code. Anonymous. Emails a one-time code to the address, for
/// a form that verifies an email field.
/// </summary>
/// <remarks>
/// Answers 202 with the same body whether the mail went out, the provider refused it or the
/// honeypot was filled. A limit is never silent: the per client limit, the per address limit and
/// the per form limit each answer 429. A form that does not verify answers 404, so this route sends
/// nothing on a form whose owner did not turn verification on.
///
/// Nothing from the request reaches the message. The recipient is the address after the email field
/// check, which admits no whitespace, and the subject and body are built from the form's own name.
/// </remarks>
internal sealed class Endpoint(
    IDocumentSession session,
    FormEmailVerifier verifier,
    ITurnstileVerifier turnstile,
    IOptions<FormsOptions> options) : Endpoint<EmailCodeRequest, EmailCodeResponse>
{
    public override void Configure()
    {
        Post("/api/public/forms/{slug}/email-code");
        AllowAnonymous();
        Options(x => x.RequireRateLimiting(FormsOptions.EmailCodeRateLimitPolicy));
    }

    public override async Task HandleAsync(EmailCodeRequest req, CancellationToken ct)
    {
        var slug = Route<string>("slug") ?? string.Empty;

        var form = await session.LoadAsync<PublicForm>(slug, ct);
        var definition = form is null
            ? null
            : await session.Query<ContentTypeDefinition>().FirstOrDefaultAsync(d => d.Name == slug, ct);

        if (definition is null || FormEmailVerifier.FieldToVerify(definition, form!) is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (!string.IsNullOrEmpty(req.Honeypot))
        {
            await Send.ResponseAsync(new EmailCodeResponse(), 202, ct);
            return;
        }

        if (options.Value.Turnstile.Enabled
            && !await turnstile.VerifyAsync(req.TurnstileToken, HttpContext.Connection.RemoteIpAddress?.ToString(), ct))
        {
            ValidationFailures.Add(new ValidationFailure("turnstileToken", "The verification challenge was not passed."));
            await Send.ErrorsAsync(400, ct);
            return;
        }

        if (!await verifier.RequestCodeAsync(definition, req.Email, ct))
        {
            await Send.StringAsync("Too many requests. Please try again later.", 429, "text/plain; charset=utf-8", ct);
            return;
        }

        await Send.ResponseAsync(new EmailCodeResponse(), 202, ct);
    }
}
