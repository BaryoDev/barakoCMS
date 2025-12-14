using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Attributes;
using Marten;
using Microsoft.Extensions.Logging;

namespace barakoCMS.Features.Workflows.Actions;

/// <summary>
/// Workflow action plugin for creating new content items.
/// Useful for auto-generating tasks, tickets, or related content based on triggers.
/// </summary>
[WorkflowActionMetadata(
    Description = "Create new content items automatically (tasks, tickets, etc.)",
    RequiredParameters = new[] { "ContentType", "Title" },
    ExampleJson = @"{""Type"":""CreateTask"",""Parameters"":{""ContentType"":""Task"",""Title"":""Review {{contentType}}"",""Status"":""Draft""}}"
)]
public class CreateTaskAction : IWorkflowAction
{
    private readonly IDocumentSession _session;
    private readonly ILogger<CreateTaskAction> _logger;

    /// <summary>
    /// Creates a new CreateTaskAction.
    /// </summary>
    public CreateTaskAction(IDocumentSession session, ILogger<CreateTaskAction> logger)
    {
        _session = session;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Type => "CreateTask";

    /// <inheritdoc />
    public async Task ExecuteAsync(Dictionary<string, string> parameters, barakoCMS.Models.Content content, CancellationToken ct)
    {
        var contentType = parameters.GetValueOrDefault("ContentType", "Task");
        var status = parameters.GetValueOrDefault("Status", "Draft");
        var title = parameters.GetValueOrDefault("Title", $"Auto-generated from {content.ContentType}");

        try
        {
            var newContent = new barakoCMS.Models.Content
            {
                Id = Guid.NewGuid(),
                ContentType = contentType,
                Status = Enum.TryParse<barakoCMS.Models.ContentStatus>(status, out var parsedStatus)
                    ? parsedStatus
                    : barakoCMS.Models.ContentStatus.Draft,
                Data = new Dictionary<string, object>
                {
                    { "Title", title },
                    { "SourceContentId", content.Id.ToString() },
                    { "SourceContentType", content.ContentType }
                },
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            // Copy additional fields from parameters
            foreach (var param in parameters.Where(p => p.Key.StartsWith("Data.")))
            {
                var fieldName = param.Key.Substring(5); // Remove "Data." prefix
                newContent.Data[fieldName] = param.Value;
            }

            _session.Store(newContent);
            await _session.SaveChangesAsync(ct);

            _logger.LogInformation(
                "Created new {ContentType} with ID {ContentId} from workflow trigger on {SourceId}",
                contentType, newContent.Id, content.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create {ContentType} from workflow", contentType);
        }
    }
}
