using barakoCMS.Core.Validation;
using FastEndpoints;
using FluentValidation;

namespace BarakoCMS.Forms.Features.Definitions;

public class FormFieldRequest
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = "string";
    public bool Required { get; set; }
}

/// <summary>The body of a create and of an update. On an update the name comes from the route.</summary>
public class FormRequest
{
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public List<FormFieldRequest> Fields { get; set; } = new();
    public List<string> NotifyAddresses { get; set; } = new();
    public bool Enabled { get; set; } = true;
}

public class FormRequestValidator : Validator<FormRequest>
{
    public FormRequestValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty()
            .MaximumLength(FormRules.MaxNameLength)
            .Matches(FormRules.FormName())
            .WithMessage("A form name is lower-case letters, digits and single hyphens, like 'contact-us'.");

        RuleFor(x => x.DisplayName).MaximumLength(FormRules.MaxDisplayNameLength);

        RuleFor(x => x.Fields)
            .NotEmpty().WithMessage("A form needs at least one field.")
            .Must(f => f.Count <= FormRules.MaxFields).WithMessage($"A form may have at most {FormRules.MaxFields} fields.")
            .Must(f => f.Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() == f.Count)
            .WithMessage("Field names must be unique.");

        RuleForEach(x => x.Fields).ChildRules(field =>
        {
            field.RuleFor(f => f.Name)
                .NotEmpty()
                .Matches(FormRules.FieldName())
                .WithMessage("A field name starts with a letter and holds letters, digits and underscores, at most 64.");
            field.RuleFor(f => f.Type)
                .Must(FieldTypeRegistry.IsKnownType)
                .WithMessage(f => $"Unknown field type '{f.Type}'. Allowed: {string.Join(", ", FieldTypeRegistry.AllowedTypeNames)}.");
        });

        RuleFor(x => x.NotifyAddresses)
            .Must(a => a.Count <= FormRules.MaxNotifyAddresses)
            .WithMessage($"At most {FormRules.MaxNotifyAddresses} notify addresses.");

        RuleForEach(x => x.NotifyAddresses)
            .Must(a => FieldTypeRegistry.IsValidValue("email", a))
            .WithMessage("Not an email address.");
    }
}

public class FormResponse
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public List<FormFieldRequest> Fields { get; set; } = new();
    public List<string> NotifyAddresses { get; set; } = new();
    public bool Enabled { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    internal static FormResponse From(FormDefinition d) => new()
    {
        Id = d.Id,
        Name = d.Name,
        DisplayName = d.DisplayName,
        Fields = d.Fields.Select(f => new FormFieldRequest { Name = f.Name, Type = f.Type, Required = f.Required }).ToList(),
        NotifyAddresses = d.NotifyAddresses.ToList(),
        Enabled = d.Enabled,
        CreatedAt = d.CreatedAt,
        UpdatedAt = d.UpdatedAt,
    };
}

internal static class FormRequestMapping
{
    /// <summary>
    /// The one rule the validator cannot hold because it needs configuration: a form may not declare
    /// the honeypot field, or real visitors could never submit it.
    /// </summary>
    public static string? HoneypotClash(FormRequest req, FormsOptions options) =>
        req.Fields.Any(f => string.Equals(f.Name, options.HoneypotField, StringComparison.OrdinalIgnoreCase))
            ? $"'{options.HoneypotField}' is the honeypot field name and cannot be a form field. Change Modules:Forms:HoneypotField or rename the field."
            : null;

    public static void Apply(FormRequest req, FormDefinition target)
    {
        target.DisplayName = req.DisplayName.Trim();
        target.Fields = req.Fields
            .Select(f => new FormField { Name = f.Name.Trim(), Type = f.Type.Trim().ToLowerInvariant(), Required = f.Required })
            .ToList();
        target.NotifyAddresses = req.NotifyAddresses
            .Select(a => a.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        target.Enabled = req.Enabled;
        target.UpdatedAt = DateTime.UtcNow;
    }
}
