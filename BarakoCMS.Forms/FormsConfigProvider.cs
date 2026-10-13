using System.Text.Json;
using barakoCMS.Core.Interfaces;
using barakoCMS.Models;
using Marten;

namespace BarakoCMS.Forms;

/// <summary>A form as its document: the email field it verifies, or none.</summary>
internal sealed record FormConfig(string? VerifyEmailField);

/// <summary>
/// The <c>forms</c> section: each content type of the tenant that accepts public submissions, under
/// the type's name.
/// </summary>
/// <remarks>
/// A form is checked the way <c>PUT /api/forms/{contentType}</c> checks one, against the type as the
/// same document leaves it, since <c>contentTypes</c> is applied first. A form the document leaves
/// out stops taking submissions, destructively, and keeps the field it verified for the day it is
/// switched on again, as turning it off through the route does.
/// </remarks>
internal sealed class FormsConfigProvider(IDocumentSession session) : IConfigProvider
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public string Section => "forms";

    public int Order => 700;

    public string ReadCapability => FormsCapabilities.ManageForms;

    public string WriteCapability => FormsCapabilities.ManageForms;

    public async Task<ConfigSectionExport?> ExportAsync(ConfigCaller caller, CancellationToken ct)
    {
        var forms = await session.Query<PublicForm>().ToListAsync(ct);
        return new ConfigSectionExport
        {
            Documents = forms.ToDictionary(
                f => f.ContentType,
                f => JsonSerializer.SerializeToElement(new FormConfig(f.VerifyEmailField), Json),
                StringComparer.Ordinal),
        };
    }

    public async Task<ConfigSectionPlan> PlanAsync(
        IReadOnlyDictionary<string, JsonElement> desired, ConfigCaller caller, CancellationToken ct)
    {
        var errors = new List<string>();
        var changes = new List<ConfigChange>();
        var stored = (await session.Query<PublicForm>().ToListAsync(ct)).ToDictionary(f => f.ContentType, StringComparer.Ordinal);

        foreach (var (name, document) in desired.OrderBy(d => d.Key, StringComparer.Ordinal))
        {
            if (await ReadAsync(name, document, errors, ct) is not { } form)
            {
                continue;
            }

            if (!stored.TryGetValue(name, out var current))
            {
                changes.Add(new ConfigChange { Key = name, Kind = ConfigChangeKind.Create, Desired = document, Summary = $"accept public submissions to '{name}'" });
            }
            else if (!string.Equals(current.VerifyEmailField, form.VerifyEmailField, StringComparison.Ordinal))
            {
                changes.Add(new ConfigChange
                {
                    Key = name, Kind = ConfigChangeKind.Change, Desired = document, Summary = $"change form '{name}'",
                    Details = [form.VerifyEmailField is null ? "stops verifying an email field" : $"verifies '{form.VerifyEmailField}'"],
                });
            }
        }

        changes.AddRange(stored.Keys.Where(name => !desired.ContainsKey(name)).Order(StringComparer.Ordinal).Select(name => new ConfigChange
        {
            Key = name, Kind = ConfigChangeKind.Destructive, Summary = $"stop accepting public submissions to '{name}'",
        }));

        return new ConfigSectionPlan { Section = Section, Changes = changes, Errors = errors };
    }

    public async Task<IReadOnlyList<string>> ApplyAsync(ConfigSectionPlan plan, ConfigCaller caller, CancellationToken ct)
    {
        var errors = new List<string>();

        foreach (var change in plan.Changes)
        {
            if (change.Desired is not { } document)
            {
                await FormEmailVerifier.RememberFieldAsync(session, change.Key, ct);
                session.Delete<PublicForm>(change.Key);
                continue;
            }

            if (await ReadAsync(change.Key, document, errors, ct) is not { } wanted)
            {
                continue;
            }

            var form = await session.LoadAsync<PublicForm>(change.Key, ct);
            if (form is null)
            {
                // A field remembered from when the form was last switched off is replaced by what the
                // document says, so it is taken and dropped.
                await FormEmailVerifier.TakeRememberedFieldAsync(session, change.Key, ct);
                form = new PublicForm
                {
                    ContentType = change.Key,
                    EnabledAt = DateTimeOffset.UtcNow,
                    EnabledBy = Guid.TryParse(caller.Principal?.FindFirst("UserId")?.Value, out var userId) ? userId : Guid.Empty,
                };
            }

            form.VerifyEmailField = wanted.VerifyEmailField;
            session.Store(form);
        }

        return errors;
    }

    /// <summary>The document, checked against the type it marks the way the route checks it, or null.</summary>
    private async Task<FormConfig?> ReadAsync(string name, JsonElement document, List<string> errors, CancellationToken ct)
    {
        FormConfig? form;
        try
        {
            form = document.ValueKind == JsonValueKind.Object ? document.Deserialize<FormConfig>(Json) : null;
        }
        catch (JsonException ex)
        {
            errors.Add($"'{name}' cannot be read: {ex.Message}");
            return null;
        }

        if (form is null)
        {
            errors.Add($"'{name}' is not a JSON object.");
            return null;
        }

        var definition = await session.Query<ContentTypeDefinition>().FirstOrDefaultAsync(d => d.Name == name, ct);
        if (definition is null)
        {
            errors.Add($"'{name}': this tenant has no content type of that name, here or earlier in the document.");
            return null;
        }

        var problems = new List<string>();
        if (definition.IsSingleton)
        {
            problems.Add($"'{name}' holds a single entry, so it could take only one submission.");
        }

        problems.AddRange(FormFields.RequiredButNotSubmittable(definition).Select(f =>
            $"'{f.Name}' is required but a visitor cannot fill it in: it is not Public, or its type '{f.Type}' is not one a form can render."));

        var verify = string.IsNullOrWhiteSpace(form.VerifyEmailField) ? null : FormEmailVerifier.EmailField(definition, form.VerifyEmailField.Trim());
        if (verify is null && !string.IsNullOrWhiteSpace(form.VerifyEmailField))
        {
            problems.Add("verifyEmailField must name an email field of this type that a visitor can fill in.");
        }

        errors.AddRange(problems.Select(p => $"'{name}': {p}"));
        return problems.Count == 0 ? new FormConfig(verify?.Name) : null;
    }
}
