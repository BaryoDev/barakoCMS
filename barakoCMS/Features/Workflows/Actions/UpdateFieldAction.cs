using Marten;
using Microsoft.Extensions.Logging;

namespace barakoCMS.Features.Workflows.Actions;

/// <summary>
/// Workflow action plugin for updating fields on content items.
/// Supports updating the triggering content or other content by ID.
/// </summary>
public class UpdateFieldAction : IWorkflowAction
{
    private readonly IDocumentSession _session;
    private readonly ILogger<UpdateFieldAction> _logger;

    /// <summary>
    /// Creates a new UpdateFieldAction.
    /// </summary>
    public UpdateFieldAction(IDocumentSession session, ILogger<UpdateFieldAction> logger)
    {
        _session = session;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Type => "UpdateField";

    /// <inheritdoc />
    public async Task ExecuteAsync(Dictionary<string, string> parameters, barakoCMS.Models.Content content, CancellationToken ct)
    {
        var targetIdStr = parameters.GetValueOrDefault("TargetId");
        var field = parameters.GetValueOrDefault("Field");
        var value = parameters.GetValueOrDefault("Value");

        if (string.IsNullOrEmpty(field))
        {
            _logger.LogWarning("UpdateField action missing required 'Field' parameter");
            return;
        }

        try
        {
            // Determine target content
            barakoCMS.Models.Content targetContent;
            if (!string.IsNullOrEmpty(targetIdStr) && Guid.TryParse(targetIdStr, out var targetId))
            {
                targetContent = await _session.LoadAsync<barakoCMS.Models.Content>(targetId, ct);
                if (targetContent == null)
                {
                    _logger.LogWarning("Target content {TargetId} not found", targetId);
                    return;
                }
            }
            else
            {
                targetContent = content; // Update the triggering content
            }

            // Handle nested field paths (e.g., "data.AssignedTo")
            if (field.StartsWith("data.", StringComparison.OrdinalIgnoreCase))
            {
                var dataKey = field.Substring(5);
                targetContent.Data[dataKey] = value;
            }
            else if (field.Equals("Status", StringComparison.OrdinalIgnoreCase))
            {
                if (Enum.TryParse<barakoCMS.Models.ContentStatus>(value, true, out var newStatus))
                {
                    targetContent.Status = newStatus;
                }
            }
            else
            {
                // Default to data field
                targetContent.Data[field] = value;
            }

            targetContent.UpdatedAt = DateTime.UtcNow;
            _session.Store(targetContent);
            await _session.SaveChangesAsync(ct);

            _logger.LogInformation(
                "Updated field {Field} on content {ContentId} to value {Value}",
                field, targetContent.Id, value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update field {Field}", field);
        }
    }
}
