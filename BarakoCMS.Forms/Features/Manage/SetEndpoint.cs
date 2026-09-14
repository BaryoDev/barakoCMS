using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using FastEndpoints;
using FluentValidation.Results;
using Marten;

namespace BarakoCMS.Forms.Features.Manage;

internal sealed class SetRequest
{
    public bool Enabled { get; set; }
}

internal sealed class FormResponse
{
    public string ContentType { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public DateTimeOffset? EnabledAt { get; set; }
}

/// <summary>
/// PUT /api/forms/{contentType}. Turns public submissions on or off for one content type.
/// </summary>
/// <remarks>
/// Refuses a type with a required field a visitor cannot fill in, because every submission to it
/// would fail validation and the form would look broken rather than misconfigured. Refuses a
/// singleton type, which could only ever take one submission.
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
            session.Delete<PublicForm>(definition.Name);
            await session.SaveChangesAsync(ct);
            await Send.OkAsync(new FormResponse { ContentType = definition.Name, Enabled = false }, ct);
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

        session.Store(form);
        await session.SaveChangesAsync(ct);

        await Send.OkAsync(new FormResponse { ContentType = form.ContentType, Enabled = true, EnabledAt = form.EnabledAt }, ct);
    }
}
