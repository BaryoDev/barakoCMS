using System.Security.Claims;
using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Attributes;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using Marten;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace barakoCMS.Features.Workflows.Actions;

/// <summary>
/// Creates an entry through the same checks as <c>POST /api/contents</c>: field sensitivity, the
/// type's validation and its lifecycle hooks. An entry the checks refuse fails the step and
/// nothing is written.
/// </summary>
[WorkflowActionMetadata(
    Description = "Create an entry of a content type, validated like any other write",
    Group = WorkflowActionGroup.Content,
    RequiredParameters = new[] { "ContentType" },
    OptionalParameters = new[] { "Status", "Data.<field>" },
    ExampleJson = @"{""Type"":""CreateEntry"",""Parameters"":{""ContentType"":""task"",""Status"":""Draft"",""Data.Title"":""Review {{data.Title}}""}}"
)]
internal class CreateEntryAction(
    IDocumentSession session,
    IContentCreator creator,
    ILogger<CreateEntryAction> logger) : IWorkflowAction
{
    /// <inheritdoc />
    public string Type => "CreateEntry";

    /// <inheritdoc />
    public async Task ExecuteAsync(Dictionary<string, string> parameters, barakoCMS.Models.Content content, CancellationToken ct)
        => await RunAsync(parameters, content, ct);

    /// <inheritdoc />
    public async Task<WorkflowActionResult> RunAsync(Dictionary<string, string> parameters, barakoCMS.Models.Content content, CancellationToken ct)
    {
        var contentType = parameters.GetValueOrDefault("ContentType");
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return WorkflowActionResult.PermanentFailure("CreateEntry needs a ContentType.");
        }

        var status = ContentStatus.Draft;
        if (parameters.GetValueOrDefault("Status") is { Length: > 0 } named
            && !Enum.TryParse(named, ignoreCase: true, out status))
        {
            return WorkflowActionResult.PermanentFailure($"'{named}' is not a content status.");
        }

        var data = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var param in parameters.Where(p => p.Key.StartsWith("Data.", StringComparison.Ordinal)))
        {
            data[param.Key["Data.".Length..]] = param.Value;
        }

        // The key is stable across reruns of this one action, so a rerun after the entry committed
        // but before the outcome was recorded finds its own entry instead of creating a second.
        Guid? entryId = parameters.TryGetValue("IdempotencyKey", out var key) && !string.IsNullOrWhiteSpace(key)
            ? new Guid(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("CreateEntry:" + key))[..16])
            : null;
        if (entryId is { } known && await session.LoadAsync<barakoCMS.Models.Content>(known, ct) is not null)
        {
            return WorkflowActionResult.Success(new Dictionary<string, string> { ["Id"] = known.ToString() });
        }

        var request = new ContentCreateRequest { Id = entryId, ContentType = contentType, Data = data, Status = status };

        // The workflow holds no user's rights: an empty principal, so field sensitivity drops what
        // an anonymous caller could not set. The events record Guid.Empty, as other system writes do.
        var context = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) };

        var errors = await creator.CheckAsync(request, Guid.Empty, context, batch: null, ct);
        if (errors.Count > 0)
        {
            return WorkflowActionResult.PermanentFailure(
                $"The {contentType} entry was refused: {string.Join("; ", errors)}");
        }

        var created = await creator.StageAsync(request, Guid.Empty, batch: null, ct);
        await session.SaveChangesAsync(ct);

        logger.LogInformation(
            "Created {ContentType} {ContentId} from workflow trigger on {SourceId}", contentType, created.Id, content.Id);

        return WorkflowActionResult.Success(new Dictionary<string, string> { ["Id"] = created.Id.ToString() });
    }
}
