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
    Group = WorkflowActionGroup.Content,
    RequiredParameters = new[] { "ContentType", "Title" },
    OptionalParameters = new[] { "Status" },
    ExampleJson = @"{""Type"":""CreateTask"",""Parameters"":{""ContentType"":""Task"",""Title"":""Review {{contentType}}"",""Status"":""Draft""}}"
)]
internal class CreateTaskAction : IWorkflowAction
{
    private readonly IDocumentSession _session;
    private readonly ILogger<CreateTaskAction> _logger;
    private readonly IContentWriter _contentWriter;

    /// <summary>
    /// Creates a new CreateTaskAction.
    /// </summary>
    public CreateTaskAction(IDocumentSession session, ILogger<CreateTaskAction> logger, IContentWriter contentWriter)
    {
        _contentWriter = contentWriter;
        _session = session;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Type => "CreateTask";

    /// <inheritdoc />
    public async Task ExecuteAsync(Dictionary<string, string> parameters, barakoCMS.Models.Content content, CancellationToken ct)
        => await RunAsync(parameters, content, ct);

    /// <inheritdoc />
    public async Task<WorkflowActionResult> RunAsync(Dictionary<string, string> parameters, barakoCMS.Models.Content content, CancellationToken ct)
    {
        var contentType = parameters.GetValueOrDefault("ContentType", "Task");

        // A parameter is text and an inline image is an object checked byte by byte, so no value
        // this action can be given is one. The type is read only when a field is copied.
        var copied = parameters.Keys.Where(k => k.StartsWith("Data.")).Select(k => k.Substring(5)).ToList();
        if (copied.Count > 0)
        {
            var definition = await _session.Query<barakoCMS.Models.ContentTypeDefinition>()
                .FirstNamedAsync(contentType, ct);
            var image = definition?.Fields.FirstOrDefault(f =>
                f is not null
                && barakoCMS.Core.Validation.InlineImageFields.Is(f.Type)
                && copied.Contains(f.Name, StringComparer.OrdinalIgnoreCase));

            if (image is not null)
            {
                return WorkflowActionResult.PermanentFailure(
                    $"Field '{image.Name}' holds an inline image, which this action cannot set.");
            }
        }

        var status = parameters.GetValueOrDefault("Status", "Draft");
        var title = parameters.GetValueOrDefault("Title", $"Auto-generated from {content.ContentType}");
        var contentId = Guid.NewGuid();
        var data = new Dictionary<string, object>
        {
            { "Title", title },
            { "SourceContentId", content.Id.ToString() },
            { "SourceContentType", content.ContentType }
        };

        // Copy additional fields from parameters
        foreach (var param in parameters.Where(p => p.Key.StartsWith("Data.")))
        {
            var fieldName = param.Key.Substring(5); // Remove "Data." prefix
            data[fieldName] = param.Value;
        }

        var refusal = await ReadJsonFieldsAsync(parameters, contentType, copied, data, ct);
        if (refusal is not null)
            return WorkflowActionResult.PermanentFailure(refusal);

        var created = new barakoCMS.Events.ContentCreated(
            contentId,
            contentType,
            data,
            Enum.TryParse<barakoCMS.Models.ContentStatus>(status, out var parsedStatus)
                ? parsedStatus
                : barakoCMS.Models.ContentStatus.Draft,
            content.LastModifiedBy,
            null,
            barakoCMS.Models.SensitivityLevel.Public, DateTime.UtcNow);

        var newContent = await _contentWriter.CreateAsync(created, ct);
        await _session.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Created new {ContentType} with ID {ContentId} from workflow trigger on {SourceId}",
            contentType, newContent.Id, content.Id);

        return WorkflowActionResult.Success();
    }

    /// <summary>
    /// A copied json field takes only a whole list or object from one placeholder, read from its
    /// JSON text. Returns the refusal, which does not name the value, or null.
    /// </summary>
    private async Task<string?> ReadJsonFieldsAsync(
        Dictionary<string, string> parameters, string contentType, List<string> copied,
        Dictionary<string, object> data, CancellationToken ct)
    {
        if (copied.Count == 0)
            return null;

        var declared = (await _session.Query<barakoCMS.Models.ContentTypeDefinition>()
            .FirstNamedAsync(contentType, ct))?.Fields;
        var sent = copied.Where(data.ContainsKey).ToDictionary(k => k, k => data[k]);
        var refusal = barakoCMS.Core.Validation.JsonTextFields.Read(
            declared, sent, key => ActionParameters.IsStructured(parameters, "Data." + key));
        if (refusal is not null)
            return refusal;

        foreach (var (key, value) in sent)
            data[key] = value;
        return null;
    }
}
