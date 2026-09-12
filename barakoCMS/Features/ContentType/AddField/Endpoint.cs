using barakoCMS.Infrastructure.Audit;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using FastEndpoints;
using FluentValidation;
using Marten;

namespace barakoCMS.Features.ContentType.AddField;

internal sealed class Request
{
    /// <summary>The content type to add to, from the route.</summary>
    public string Name { get; set; } = string.Empty;

    public string FieldName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Any name FieldTypeRegistry accepts. Validated against the same registry as create.</summary>
    public string Type { get; set; } = "text";

    /// <summary>Required for a reference, meaningless otherwise.</summary>
    public string? ReferenceType { get; set; }

    public bool IsRequired { get; set; }
    public object? DefaultValue { get; set; }
    public Dictionary<string, object>? ValidationRules { get; set; }

    /// <summary>Field-level sensitivity, so a field can arrive masked rather than be masked later.</summary>
    public SensitivityLevel Sensitivity { get; set; } = SensitivityLevel.Public;
    public List<string>? VisibleToRoles { get; set; }
}

internal sealed class Response
{
    public string ContentType { get; init; } = string.Empty;

    /// <summary>The field this call added.</summary>
    public string Added { get; init; } = string.Empty;

    /// <summary>Every field the type now has, in order.</summary>
    public List<string> Fields { get; init; } = new();
}

internal sealed class Validator : Validator<Request>
{
    public Validator()
    {
        RuleFor(x => x.FieldName).NotEmpty().WithMessage("fieldName is required.");
        RuleFor(x => x.Type).NotEmpty().WithMessage("type is required.");
    }
}

/// <summary>
/// POST /api/content-types/{name}/fields. Adds one field to a content type that already exists.
/// </summary>
/// <remarks>
/// The missing half of "content types are defined at runtime".
///
/// A type could be created and its fields read, but nothing could add one afterwards. The only way
/// to gain a field was the SEO endpoint, which does exactly this mutation for one hardcoded set, so
/// the capability was already here and only the general case was missing. Without it a client
/// asking for one more field on a type that already holds their content had no answer that did not
/// involve recreating the type, and a page type could never gain the field a block or widget list
/// needs.
///
/// Three things this deliberately refuses.
///
/// A name the type already has, rather than overwriting it. The existing field may have been
/// renamed, made required or given rules by someone else, and none of that is this endpoint's to
/// undo. Changing a field is a different operation with data consequences and does not exist yet.
///
/// A required field with no default, on a type that already has entries. Every one of those entries
/// is missing the new field, so the type would be declaring an invariant its own content breaks. A
/// default makes the rule honest, and an optional field is always safe.
///
/// Anything the create validator would refuse. The merged field list goes through
/// IContentTypeValidatorService, the same call create makes, so a bad type name, a reference with no
/// target, or a duplicate under a different casing is rejected here for the same reason and with the
/// same message.
///
/// Adding an endpoint and adding an optional field to a response are both additive, so the API
/// contract version does not move.
/// </remarks>
internal sealed class Endpoint : Endpoint<Request, Response>
{
    private readonly IDocumentSession _session;
    private readonly barakoCMS.Infrastructure.Services.IContentTypeValidatorService _validator;
    private readonly barakoCMS.Infrastructure.Multitenancy.TenantContext _tenant;

    public Endpoint(
        IDocumentSession session,
        barakoCMS.Infrastructure.Services.IContentTypeValidatorService validator,
        barakoCMS.Infrastructure.Multitenancy.TenantContext tenant)
    {
        _session = session;
        _validator = validator;
        _tenant = tenant;
    }

    public override void Configure()
    {
        Post("/api/content-types/{name}/fields");
        // Adding a field to a content type is what manage_content_types means. Same gate as the
        // SEO endpoint next door, which does the same thing for a fixed set.
        Definition.RequireCapability(SystemCapabilities.ManageContentTypes, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var name = barakoCMS.Core.ContentTypeName.Normalize(req.Name);

        var definition = await _session.Query<ContentTypeDefinition>()
            .FirstOrDefaultAsync(d => d.Name.ToLower() == name, ct);

        if (definition is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var clash = definition.Fields
            .FirstOrDefault(f => string.Equals(f.Name, req.FieldName, StringComparison.OrdinalIgnoreCase));

        if (clash is not null)
        {
            AddError(
                $"The content type \"{definition.Name}\" already has a field named \"{clash.Name}\". "
                + "Changing an existing field is a separate operation and is not supported here.");
            ThrowIfAnyErrors(StatusCodes.Status409Conflict);
        }

        var field = new FieldDefinition
        {
            Name = req.FieldName,
            DisplayName = string.IsNullOrWhiteSpace(req.DisplayName) ? req.FieldName : req.DisplayName,
            Type = req.Type,
            ReferenceType = req.ReferenceType,
            IsRequired = req.IsRequired,
            DefaultValue = req.DefaultValue,
            ValidationRules = req.ValidationRules ?? new Dictionary<string, object>(),
            Sensitivity = req.Sensitivity,
            VisibleToRoles = req.VisibleToRoles ?? new List<string>(),
        };

        // Validate the type as it would be, not the field on its own, so every rule create applies
        // applies here too.
        var merged = definition.Fields.Concat(new[] { field }).ToList();
        var (isValid, errors) = _validator.Validate(definition.Name, definition.DisplayName, merged);
        if (!isValid)
        {
            foreach (var error in errors) AddError(error);
            ThrowIfAnyErrors();
        }

        if (field.IsRequired && field.DefaultValue is null)
        {
            var entries = await _session.Query<barakoCMS.Models.Content>()
                .CountAsync(c => c.ContentType == definition.Name, ct);

            if (entries > 0)
            {
                AddError(
                    $"\"{definition.Name}\" already has {entries} "
                    + (entries == 1 ? "entry" : "entries")
                    + $" with no \"{field.Name}\", so a required field with no default would make them "
                    + "all invalid. Give the field a defaultValue, or add it as optional.");
                ThrowIfAnyErrors();
            }
        }

        definition.Fields.Add(field);
        definition.UpdatedAt = DateTime.UtcNow;
        _session.Store(definition);

        var actorId = Guid.TryParse(User.FindFirst("UserId")?.Value, out var parsed) ? parsed : (Guid?)null;
        await AuditLog.RecordAsync(_session, _tenant.Slug, "contenttype.field_added", actorId,
            User.FindFirst("Username")?.Value,
            targetType: nameof(ContentTypeDefinition), targetId: definition.Name,
            metadata: new Dictionary<string, object>
            {
                ["field"] = field.Name,
                ["type"] = field.Type,
                ["required"] = field.IsRequired,
            }, ct: ct);

        await _session.SaveChangesAsync(ct);

        await Send.OkAsync(new Response
        {
            ContentType = definition.Name,
            Added = field.Name,
            Fields = definition.Fields.Select(f => f.Name).ToList(),
        }, ct);
    }
}
