using FluentValidation;

namespace barakoCMS.Features.ContentType.Create;

public class Request
{
    public string Name { get; set; } = string.Empty;
    public Dictionary<string, string> Fields { get; set; } = new();
}

public class RequestValidator : FastEndpoints.Validator<Request>
{
    public RequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty();
        RuleFor(x => x.Fields).NotEmpty();
        
        // Validate field types are allowed
        RuleFor(x => x.Fields)
            .Must(fields => fields.All(f => barakoCMS.Core.Validation.FieldTypeValidator.IsValidFieldType(f.Value)))
            .WithMessage(req =>
            {
                var invalidTypes = barakoCMS.Core.Validation.FieldTypeValidator.GetInvalidFieldTypes(req.Fields);
                return string.Join("; ", invalidTypes);
            })
            .When(x => x.Fields != null && x.Fields.Any());
        
        // Validate field names are PascalCase
        RuleFor(x => x.Fields)
            .Must(fields => fields.All(f => barakoCMS.Core.Validation.FieldTypeValidator.IsValidFieldName(f.Key)))
            .WithMessage(req =>
            {
                var invalidNames = barakoCMS.Core.Validation.FieldTypeValidator.GetInvalidFieldNames(req.Fields);
                return string.Join("; ", invalidNames);
            })
            .When(x => x.Fields != null && x.Fields.Any());
    }
}

public class Response
{
    public Guid Id { get; set; }
    public string Message { get; set; } = string.Empty;
}
