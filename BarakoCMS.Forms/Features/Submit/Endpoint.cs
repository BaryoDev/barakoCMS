using System.Net;
using System.Text;
using System.Text.Json;
using barakoCMS.Core.Interfaces;
using barakoCMS.Core.Validation;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using FastEndpoints;
using FluentValidation.Results;
using Marten;
// RequireRateLimiting lives here; this project is not a Web SDK project, so it is not implicitly used.
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BarakoCMS.Forms.Features.Submit;

public class SubmitResponse
{
    public Guid Id { get; set; }
}

/// <summary>
/// POST /api/public/forms/{name}. Anonymous, because a visitor is not signed in, and treated as a
/// target for the same reason.
/// </summary>
/// <remarks>
/// In order: the body is read up to a cap and refused at 413 past it; a value in the honeypot
/// field is answered 202 and dropped; the form has to exist and be enabled; every field is checked
/// against the definition (unknown field, missing required field, wrong type, too long: all 400);
/// the submission is stored Sensitive; and the notify addresses are emailed best effort. The rate
/// limit sits in front of all of it, per client address.
///
/// Nothing a visitor typed reaches a log line. The 202 carries the submission id and nothing else.
/// </remarks>
public class Endpoint : EndpointWithoutRequest<SubmitResponse>
{
    private readonly IDocumentSession _session;
    private readonly IEmailService _email;
    private readonly IOptions<FormsOptions> _options;
    private readonly TenantContext _tenant;
    private readonly ILogger<Endpoint> _logger;

    public Endpoint(
        IDocumentSession session,
        IEmailService email,
        IOptions<FormsOptions> options,
        TenantContext tenant,
        ILogger<Endpoint> logger)
    {
        _session = session;
        _email = email;
        _options = options;
        _tenant = tenant;
        _logger = logger;
    }

    public override void Configure()
    {
        Post("/api/public/forms/{name}");
        AllowAnonymous();
        // Unauthenticated and a row per call, so a tighter budget than the global per-address one.
        Options(x => x.RequireRateLimiting(FormsModule.RateLimitPolicy));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var options = _options.Value;
        var name = Route<string>("name") ?? string.Empty;

        var body = await ReadBodyAsync(options.MaxBodyBytes, ct);
        if (body is null)
        {
            await Send.ResponseAsync(new SubmitResponse(), StatusCodes.Status413PayloadTooLarge, ct);
            return;
        }

        Dictionary<string, JsonElement>? fields;
        try
        {
            fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body);
        }
        catch (JsonException)
        {
            fields = null;
        }

        if (fields is null)
        {
            AddError("The body must be a JSON object of field values.");
            await Send.ErrorsAsync(400, ct);
            return;
        }

        if (IsHoneypotHit(fields, options.HoneypotField))
        {
            // Acknowledged like a real one, stored like nothing. A different answer would tell the
            // sender which field to leave blank next time.
            await Send.ResponseAsync(new SubmitResponse { Id = Guid.NewGuid() }, StatusCodes.Status202Accepted, ct);
            return;
        }

        var form = await _session.Query<FormDefinition>().FirstOrDefaultAsync(f => f.Name == name, ct);
        if (form is null || !form.Enabled) { await Send.NotFoundAsync(ct); return; }

        var (data, failures) = SubmissionValidator.Validate(form, fields, options);
        if (failures.Count > 0)
        {
            foreach (var failure in failures) ValidationFailures.Add(failure);
            await Send.ErrorsAsync(400, ct);
            return;
        }

        var submission = new FormSubmission
        {
            FormId = form.Id,
            FormName = form.Name,
            Data = data,
            Sensitivity = SensitivityLevel.Sensitive,
        };
        _session.Store(submission);
        await _session.SaveChangesAsync(ct);

        await NotifyAsync(form, submission, options, ct);

        await Send.ResponseAsync(new SubmitResponse { Id = submission.Id }, StatusCodes.Status202Accepted, ct);
    }

    /// <summary>The body as text, or null when it is larger than <paramref name="maxBytes"/>.</summary>
    /// <remarks>
    /// Read here rather than bound, so the cap is checked before anything is deserialized and holds
    /// whether or not the host in front enforces a request size. Content-Length is honoured when
    /// present and the stream is counted regardless, since a chunked body has no length to declare.
    /// </remarks>
    private async Task<string?> ReadBodyAsync(int maxBytes, CancellationToken ct)
    {
        var request = HttpContext.Request;
        if (request.ContentLength is { } declared && declared > maxBytes) return null;

        using var buffer = new MemoryStream();
        var chunk = new byte[8 * 1024];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > maxBytes) return null;
            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static bool IsHoneypotHit(Dictionary<string, JsonElement> fields, string honeypot)
    {
        foreach (var (key, value) in fields)
        {
            if (!string.Equals(key, honeypot, StringComparison.OrdinalIgnoreCase)) continue;

            return value.ValueKind switch
            {
                JsonValueKind.Null or JsonValueKind.Undefined => false,
                JsonValueKind.String => !string.IsNullOrWhiteSpace(value.GetString()),
                _ => true,
            };
        }

        return false;
    }

    /// <summary>
    /// One email per notify address, awaited with a timeout so a slow provider delays the 202 by
    /// at most that long and a failing one never fails it. A queue lands with #106.
    /// </summary>
    private async Task NotifyAsync(FormDefinition form, FormSubmission submission, FormsOptions options, CancellationToken ct)
    {
        if (form.NotifyAddresses.Count == 0) return;

        var subject = $"New submission: {(string.IsNullOrWhiteSpace(form.DisplayName) ? form.Name : form.DisplayName)}";
        var body = NotificationBody(form, submission);
        var timeout = TimeSpan.FromSeconds(options.NotifyTimeoutSeconds > 0
            ? options.NotifyTimeoutSeconds
            : FormsOptions.DefaultNotifyTimeoutSeconds);

        foreach (var address in form.NotifyAddresses)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(timeout);
            try
            {
                await _email.SendEmailAsync(address, subject, body, linked.Token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // Form and tenant only. The recipient is an operator's address and the body is a
                // visitor's data; neither belongs in a log line.
                _logger.LogWarning(ex,
                    "A form notification for '{Form}' in tenant '{Tenant}' was not sent. The submission {SubmissionId} is stored.",
                    form.Name, _tenant.Slug, submission.Id);
            }
        }
    }

    private static string NotificationBody(FormDefinition form, FormSubmission submission)
    {
        var sb = new StringBuilder();
        sb.Append("<p>A new submission arrived for <strong>")
          .Append(WebUtility.HtmlEncode(form.Name))
          .Append("</strong> at ")
          .Append(submission.SubmittedAt.ToString("u"))
          .Append(".</p><table>");

        foreach (var field in form.Fields)
        {
            submission.Data.TryGetValue(field.Name, out var value);
            sb.Append("<tr><th align=\"left\">")
              .Append(WebUtility.HtmlEncode(field.Name))
              .Append("</th><td>")
              .Append(WebUtility.HtmlEncode(SubmissionValidator.Display(value)))
              .Append("</td></tr>");
        }

        sb.Append("</table><p>Submission id ").Append(submission.Id).Append(".</p>");
        return sb.ToString();
    }
}

/// <summary>
/// Checks a visitor's fields against the definition and turns them into the CLR shapes the
/// document stores. Pure, so it can be unit tested without a host.
/// </summary>
public static class SubmissionValidator
{
    public static (Dictionary<string, object> Data, List<ValidationFailure> Failures) Validate(
        FormDefinition form, Dictionary<string, JsonElement> fields, FormsOptions options)
    {
        var data = new Dictionary<string, object>();
        var failures = new List<ValidationFailure>();
        var byName = form.Fields.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
        var maxChars = options.MaxFieldChars > 0 ? options.MaxFieldChars : FormsOptions.DefaultMaxFieldChars;

        foreach (var (key, value) in fields)
        {
            if (string.Equals(key, options.HoneypotField, StringComparison.OrdinalIgnoreCase)) continue;

            if (!byName.TryGetValue(key, out var field))
            {
                failures.Add(new ValidationFailure(key, "This form has no such field."));
                continue;
            }

            if (IsEmpty(value)) continue;

            if (value.GetRawText().Length > maxChars)
            {
                failures.Add(new ValidationFailure(field.Name, $"At most {maxChars} characters."));
                continue;
            }

            if (!FieldTypeRegistry.IsValidValue(field.Type, value))
            {
                failures.Add(new ValidationFailure(field.Name, $"Not a valid {field.Type}."));
                continue;
            }

            data[field.Name] = ToClr(value);
        }

        foreach (var field in form.Fields)
        {
            if (field.Required && !data.ContainsKey(field.Name))
                failures.Add(new ValidationFailure(field.Name, "This field is required."));
        }

        return (data, failures);
    }

    private static bool IsEmpty(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => true,
        JsonValueKind.String => string.IsNullOrWhiteSpace(value.GetString()),
        _ => false,
    };

    /// <summary>
    /// The same shapes <c>ObjectJsonConverter</c> produces when a stored document is read back, so
    /// a value looks the same on the way in as on the way out.
    /// </summary>
    private static object ToClr(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString()!.Trim(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number when value.TryGetInt64(out var l) => l,
        JsonValueKind.Number when value.TryGetDecimal(out var d) => d,
        JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.Object => value.EnumerateObject()
            .Where(p => p.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            .ToDictionary(p => p.Name, p => ToClr(p.Value)),
        JsonValueKind.Array => value.EnumerateArray()
            .Where(e => e.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            .Select(ToClr).ToList(),
        _ => value.GetRawText(),
    };

    public static string Display(object? value) => value switch
    {
        null => string.Empty,
        string s => s,
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
        _ => JsonSerializer.Serialize(value),
    };
}
