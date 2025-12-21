using FluentValidation;
using Marten;

namespace barakoCMS.Features.Content.Update;

public class Request
{
    public Guid Id { get; set; }
    public Dictionary<string, object> Data { get; set; } = new();
    public Models.ContentStatus Status { get; set; }
    public long Version { get; set; }
}

public class RequestValidator : FastEndpoints.Validator<Request>
{
    private readonly IDocumentSession _session;

    public RequestValidator(IDocumentSession session)
    {
        _session = session;

        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Data).NotEmpty();

        // Async validation against existing Content's ContentType
        RuleFor(x => x)
            .MustAsync(async (req, ct) => await ValidateDataAgainstExistingContentType(req, ct))
            .WithMessage(req => GetSchemaValidationErrors(req).Result);
    }

    private async Task<bool> ValidateDataAgainstExistingContentType(Request req, CancellationToken ct)
    {
        // Find the existing content
        var content = await _session.LoadAsync<barakoCMS.Models.Content>(req.Id, ct);

        if (content == null)
        {
            // Content doesn't exist - let the endpoint handle this error
            return true;
        }

        // Find the ContentType (async query)
        var contentType = await _session.Query<barakoCMS.Models.ContentType>()
            .FirstOrDefaultAsync(c => c.Slug == content.ContentType, ct);

        if (contentType == null)
        {
            // ContentType doesn't exist (shouldn't happen)
            return true;
        }

        // Validate data against field definitions
        var result = barakoCMS.Core.Validation.ContentDataValidator.ValidateData(
            req.Data,
            contentType.Fields);

        return result.IsValid;
    }

    private async Task<string> GetSchemaValidationErrors(Request req)
    {
        var content = await _session.LoadAsync<barakoCMS.Models.Content>(req.Id);

        if (content == null)
            return $"Content with ID '{req.Id}' not found";

        var contentType = await _session.Query<barakoCMS.Models.ContentType>()
            .FirstOrDefaultAsync(c => c.Slug == content.ContentType);

        if (contentType == null)
            return $"ContentType '{content.ContentType}' not found";

        var result = barakoCMS.Core.Validation.ContentDataValidator.ValidateData(
            req.Data,
            contentType.Fields);

        return string.Join("; ", result.Errors);
    }
}

public class Response
{
    public Guid Id { get; set; }
    public string Message { get; set; } = string.Empty;
}
