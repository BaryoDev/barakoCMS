using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using FastEndpoints;
using FluentValidation.Results;
using Marten;

namespace BarakoCMS.Forms.Features.Manage;

internal sealed class SetRequest
{
    public bool Enabled { get; set; }

    /// <summary>
    /// The email field to verify with an emailed code before a submission is accepted. Left out, the
    /// form keeps what it has. An empty string turns verification off.
    /// </summary>
    public string? VerifyEmailField { get; set; }
}

internal sealed class FormResponse
{
    public string ContentType { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public DateTimeOffset? EnabledAt { get; set; }
    public string? VerifyEmailField { get; set; }
}

/// <summary>
/// PUT /api/forms/{contentType}. Turns public submissions on or off for one content type.
/// </summary>
/// <remarks>
/// Refuses a type with a required field a visitor cannot fill in, because every submission to it
/// would fail validation and the form would look broken rather than misconfigured. Refuses a
/// singleton type, which could only ever take one submission. Refuses to verify a field that is not
/// an email field a visitor can fill in, since no submission could ever pass.
///
/// Turning a form off deletes its row, as it always has, and keeps the field it verified beside
/// its send count. Turning it on again without <c>verifyEmailField</c> puts that field back, so a
/// client that has never heard of the setting cannot drop verification by toggling the form.
/// </remarks>
internal sealed class SetEndpoint(IDocumentSession session) : Endpoint<SetRequest, FormResponse>
{
    public override void Configure()
    {
        Put("/api/forms/{contentType}");
        Definition.RequireCapability(FormsCapabilities.ManageForms, FormsCapabilities.LegacyRoles);
    }

    public override async Task HandleAsync(SetRequest req, CancellationToken ct)
    {
        var name = Route<string>("contentType") ?? string.Empty;

        var definition = await session.Query<ContentTypeDefinition>().FirstOrDefaultAsync(d => d.Name == name, ct);
        if (definition is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (!req.Enabled)
        {
            var kept = await FormEmailVerifier.RememberFieldAsync(session, definition.Name, ct);
            session.Delete<PublicForm>(definition.Name);
            await session.SaveChangesAsync(ct);
            await Send.OkAsync(new FormResponse { ContentType = definition.Name, Enabled = false, VerifyEmailField = kept }, ct);
            return;
        }

        if (definition.IsSingleton)
        {
            ValidationFailures.Add(new ValidationFailure("contentType",
                $"'{definition.Name}' holds a single entry, so it could take only one submission."));
        }

        foreach (var field in FormFields.RequiredButNotSubmittable(definition))
        {
            ValidationFailures.Add(new ValidationFailure($"fields.{field.Name}",
                $"'{field.Name}' is required but a visitor cannot fill it in: it is not Public, or its type "
              + $"'{field.Type}' is not one a form can render."));
        }

        var verifyField = string.IsNullOrWhiteSpace(req.VerifyEmailField)
            ? null
            : FormEmailVerifier.EmailField(definition, req.VerifyEmailField.Trim());
        if (verifyField is null && !string.IsNullOrWhiteSpace(req.VerifyEmailField))
        {
            ValidationFailures.Add(new ValidationFailure("verifyEmailField",
                "verifyEmailField must name an email field of this type that a visitor can fill in."));
        }

        if (ValidationFailures.Count > 0)
        {
            await Send.ErrorsAsync(400, ct);
            return;
        }

        var form = await session.LoadAsync<PublicForm>(definition.Name, ct) ?? new PublicForm
        {
            ContentType = definition.Name,
            EnabledAt = DateTimeOffset.UtcNow,
            EnabledBy = Guid.TryParse(User.FindFirst("UserId")?.Value, out var userId) ? userId : Guid.Empty,
        };

        var remembered = await FormEmailVerifier.TakeRememberedFieldAsync(session, definition.Name, ct);
        if (req.VerifyEmailField is not null)
        {
            form.VerifyEmailField = verifyField?.Name;
        }
        else if (remembered is not null)
        {
            var restored = FormEmailVerifier.EmailField(definition, remembered);
            if (restored is null)
            {
                ValidationFailures.Add(new ValidationFailure("verifyEmailField",
                    "This form verified a field that is no longer an email field a visitor can fill in. "
                  + "Send verifyEmailField to name one, or an empty string to turn verification off."));
                await Send.ErrorsAsync(400, ct);
                return;
            }

            form.VerifyEmailField = restored.Name;
        }

        session.Store(form);
        await session.SaveChangesAsync(ct);

        await Send.OkAsync(new FormResponse
        {
            ContentType = form.ContentType,
            Enabled = true,
            EnabledAt = form.EnabledAt,
            VerifyEmailField = form.VerifyEmailField,
        }, ct);
    }
}
