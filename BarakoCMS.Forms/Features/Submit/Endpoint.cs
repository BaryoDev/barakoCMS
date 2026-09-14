using System.Text.Json;
using barakoCMS.Core.Interfaces;
using barakoCMS.Core.Validation;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FastEndpoints;
using FluentValidation.Results;
using Marten;
// RequireRateLimiting lives here; this project is not a Web SDK project, so it is not implicitly used.
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Options;

namespace BarakoCMS.Forms.Features.Submit;

internal sealed class Request
{
    /// <summary>The field values, keyed by field name.</summary>
    public Dictionary<string, object?> Data { get; set; } = new();

    /// <summary>Left empty by a person. The widget renders it hidden; a bot filling every input sets it.</summary>
    public string? Honeypot { get; set; }

    /// <summary>The Cloudflare Turnstile token, required only when verification is configured.</summary>
    public string? TurnstileToken { get; set; }
}

internal sealed class Response
{
    public bool Accepted { get; set; } = true;
}

/// <summary>
/// POST /api/public/forms/{slug}. Anonymous. Stores the submission as a Draft entry of the form's
/// content type with document sensitivity Sensitive, so public delivery never serves it back.
/// </summary>
/// <remarks>
/// The request has no status, sensitivity or owner, so a caller cannot choose them: status is always
/// Draft, sensitivity always Sensitive and the owner always empty. Unknown fields are refused rather
/// than dropped, with the same message as a field that exists but is not submittable, so the refusal
/// does not tell a stranger which hidden fields a type has.
/// </remarks>
internal sealed class Endpoint(
    IDocumentSession session,
    IContentWriter contentWriter,
    IContentValidatorService validator,
    IContentLifecycleRunner lifecycle,
    ITurnstileVerifier turnstile,
    IOptions<FormsOptions> options) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Post("/api/public/forms/{slug}");
        AllowAnonymous();
        Options(x => x.RequireRateLimiting(FormsOptions.RateLimitPolicy));
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var slug = Route<string>("slug") ?? string.Empty;

        var form = await session.LoadAsync<PublicForm>(slug, ct);
        var definition = form is null
            ? null
            : await session.Query<ContentTypeDefinition>().FirstOrDefaultAsync(d => d.Name == slug, ct);

        if (definition is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // Answered exactly as an accepted submission, so a bot learns nothing from the difference.
        if (!string.IsNullOrEmpty(req.Honeypot))
        {
            await Send.ResponseAsync(new Response(), 202, ct);
            return;
        }

        var settings = options.Value;

        if (settings.Turnstile.Enabled
            && !await turnstile.VerifyAsync(req.TurnstileToken, HttpContext.Connection.RemoteIpAddress?.ToString(), ct))
        {
            ValidationFailures.Add(new ValidationFailure("turnstileToken", "The verification challenge was not passed."));
            await Send.ErrorsAsync(400, ct);
            return;
        }

        var data = CheckFields(definition, req.Data ?? new(), settings.MaxFieldLength);
        if (ValidationFailures.Count > 0)
        {
            await Send.ErrorsAsync(400, ct);
            return;
        }

        // The rest of the schema rules (the singleton cap among them) and any module's domain rules,
        // exactly as an authenticated create runs them.
        var (isValid, schemaErrors) = await validator.ValidateAsync(definition.Name, data, existing: null);
        if (!isValid)
        {
            foreach (var error in schemaErrors)
            {
                AddError(error);
            }
            await Send.ErrorsAsync(400, ct);
            return;
        }

        var hookErrors = await lifecycle.RunBeforeSaveAsync(definition.Name, entryId: null, data, existing: null, Guid.Empty, ct);
        if (hookErrors.Count > 0)
        {
            foreach (var error in hookErrors)
            {
                AddError(error);
            }
            await Send.ErrorsAsync(400, ct);
            return;
        }

        var searchText = string.Join(' ', data.Values
            .Select(v => v is JsonElement je ? je.ToString() : v?.ToString())
            .Where(v => !string.IsNullOrWhiteSpace(v)));

        var created = await contentWriter.CreateAsync(new barakoCMS.Events.ContentCreated(
            Guid.NewGuid(),
            definition.Name,
            data,
            ContentStatus.Draft,
            Guid.Empty,
            searchText,
            SensitivityLevel.Sensitive,
            DateTime.UtcNow), ct);

        if (definition.Lifecycle is { } states)
        {
            created.LifecycleState = states.InitialState;
            session.Store(created);
        }

        await session.SaveChangesAsync(ct);

        await Send.ResponseAsync(new Response(), 202, ct);
    }

    /// <summary>
    /// Checks each submitted value against the field it names, recording a failure per field, and
    /// returns the values keyed by the schema's spelling of each name.
    /// </summary>
    private Dictionary<string, object> CheckFields(ContentTypeDefinition definition, Dictionary<string, object?> submitted, int maxLength)
    {
        var fields = FormFields.Submittable(definition)
            .ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
        var data = new Dictionary<string, object>();

        foreach (var key in submitted.Keys.Where(k => !fields.ContainsKey(k)))
        {
            ValidationFailures.Add(new ValidationFailure($"data.{key}", $"'{key}' is not a field this form accepts."));
        }

        foreach (var field in fields.Values)
        {
            var value = submitted.FirstOrDefault(kv => kv.Key.Equals(field.Name, StringComparison.OrdinalIgnoreCase)).Value;
            if (value is JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined })
            {
                value = null;
            }

            var text = value is JsonElement { ValueKind: JsonValueKind.String } s ? s.GetString() : null;
            var blank = value is null || (text is not null && string.IsNullOrWhiteSpace(text));

            if (blank)
            {
                if (field.IsRequired)
                {
                    ValidationFailures.Add(new ValidationFailure($"data.{field.Name}", $"'{Label(field)}' is required."));
                }
                continue;
            }

            if (text is not null && text.Length > maxLength)
            {
                ValidationFailures.Add(new ValidationFailure($"data.{field.Name}", $"'{Label(field)}' is longer than {maxLength} characters."));
                continue;
            }

            if (!FieldTypeRegistry.IsValidValue(field.Type, value!))
            {
                ValidationFailures.Add(new ValidationFailure($"data.{field.Name}", $"'{Label(field)}' is not a valid {field.Type.ToLowerInvariant()}."));
                continue;
            }

            data[field.Name] = value!;
        }

        return data;
    }

    private static string Label(FieldDefinition field) =>
        string.IsNullOrWhiteSpace(field.DisplayName) ? field.Name : field.DisplayName;
}
