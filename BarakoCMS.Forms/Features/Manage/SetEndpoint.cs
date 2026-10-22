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

    /// <summary>
    /// The singleton content type that holds the opening and closing dates. Sent with
    /// <c>opensAtField</c> and <c>closesAtField</c> as one group: leave all three out to keep the
    /// window, send any of them to replace it, and an empty string clears one.
    /// </summary>
    public string? WindowContentType { get; set; }

    /// <summary>A date or datetime field of the window type. Submissions before its value are refused.</summary>
    public string? OpensAtField { get; set; }

    /// <summary>A date or datetime field of the window type. Submissions from its value on are refused.</summary>
    public string? ClosesAtField { get; set; }
}

internal sealed class FormResponse
{
    public string ContentType { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public DateTimeOffset? EnabledAt { get; set; }
    public string? VerifyEmailField { get; set; }
    public string? WindowContentType { get; set; }
    public string? OpensAtField { get; set; }
    public string? ClosesAtField { get; set; }
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
        Definition.RequireCapability(FormsCapabilities.ManageForms, FormsCapabilities.Defaults.LegacyRoles);
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

        var changesWindow = req.WindowContentType is not null || req.OpensAtField is not null || req.ClosesAtField is not null;
        if (changesWindow)
        {
            await CheckWindowAsync(req, ct);
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

        if (changesWindow)
        {
            form.WindowContentType = Blank(req.WindowContentType);
            form.OpensAtField = Blank(req.OpensAtField);
            form.ClosesAtField = Blank(req.ClosesAtField);
            if (!FormWindow.IsConfigured(form))
            {
                form.WindowContentType = form.OpensAtField = form.ClosesAtField = null;
            }
        }

        session.Store(form);
        await session.SaveChangesAsync(ct);

        await Send.OkAsync(new FormResponse
        {
            ContentType = form.ContentType,
            Enabled = true,
            EnabledAt = form.EnabledAt,
            VerifyEmailField = form.VerifyEmailField,
            WindowContentType = form.WindowContentType,
            OpensAtField = form.OpensAtField,
            ClosesAtField = form.ClosesAtField,
        }, ct);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private async Task CheckWindowAsync(SetRequest req, CancellationToken ct)
    {
        var typeName = Blank(req.WindowContentType);
        var opens = Blank(req.OpensAtField);
        var closes = Blank(req.ClosesAtField);

        if (typeName is null)
        {
            if (opens is not null || closes is not null)
            {
                ValidationFailures.Add(new ValidationFailure("windowContentType",
                    "windowContentType must name the singleton content type that holds the dates."));
            }
            return;
        }

        var window = await session.Query<ContentTypeDefinition>().FirstOrDefaultAsync(d => d.Name == typeName, ct);
        if (window is null || !window.IsSingleton)
        {
            ValidationFailures.Add(new ValidationFailure("windowContentType",
                "windowContentType must name a singleton content type."));
            return;
        }

        if (opens is null && closes is null)
        {
            ValidationFailures.Add(new ValidationFailure("opensAtField",
                "Name opensAtField, closesAtField or both, or send an empty windowContentType to clear the window."));
            return;
        }

        foreach (var (property, name) in new[] { ("opensAtField", opens), ("closesAtField", closes) })
        {
            if (name is not null && !window.Fields.Any(f => f.Name == name && FormWindow.IsDateField(f)))
            {
                ValidationFailures.Add(new ValidationFailure(property,
                    $"'{name}' is not a date or datetime field of '{window.Name}'."));
            }
        }
    }
}
