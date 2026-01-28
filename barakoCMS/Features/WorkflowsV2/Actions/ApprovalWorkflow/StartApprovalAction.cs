using barakoCMS.Features.WorkflowsV2.Models;
using barakoCMS.Features.WorkflowsV2.Services;
using Microsoft.Extensions.Logging;

namespace barakoCMS.Features.WorkflowsV2.Actions.ApprovalWorkflow;

/// <summary>
/// Start an approval workflow for the content.
/// </summary>
public class StartApprovalAction : BaseWorkflowAction
{
    private readonly IApprovalService _approvalService;

    public StartApprovalAction(
        IApprovalService approvalService,
        ILogger<StartApprovalAction> logger) : base(logger)
    {
        _approvalService = approvalService;
    }

    public override string Type => "StartApproval";
    public override string Name => "Start Approval";
    public override string Description => "Create an approval request for the content.";
    public override string Category => ActionCategories.ApprovalWorkflow;

    public override async Task<ActionResult> ExecuteAsync(WorkflowActionV2 action, WorkflowContext context)
    {
        try
        {
            var approversList = GetStringList(action.Config, "approvers", context);
            var title = GetString(action.Config, "title", context,
                $"Approval required for {context.Content.ContentType}");
            var description = GetString(action.Config, "description", context);
            var expiresIn = GetString(action.Config, "expiresIn", context, "48h");
            var reminderAfter = GetString(action.Config, "reminderAfter", context);
            var approvalTypeStr = GetString(action.Config, "approvalType", context, "any");
            var threshold = GetInt(action.Config, "threshold", 0);

            if (approversList.Count == 0)
            {
                return Failure("No approvers specified.");
            }

            var approvalType = approvalTypeStr.ToLowerInvariant() switch
            {
                "all" => ApprovalType.All,
                "sequential" => ApprovalType.Sequential,
                "threshold" => ApprovalType.Threshold,
                _ => ApprovalType.Any
            };

            // Parse expiration
            var expiresAt = ParseDuration(expiresIn);
            var reminderAt = string.IsNullOrEmpty(reminderAfter) ? null : ParseDuration(reminderAfter);

            // Build approvers
            var approvers = new List<Approver>();
            int order = 0;
            foreach (var approverSpec in approversList)
            {
                approvers.Add(ParseApprover(approverSpec, order++));
            }

            // Parse outcome actions
            var onApprove = ParseOutcomeActions(action.Config, "onApprove", context);
            var onReject = ParseOutcomeActions(action.Config, "onReject", context);
            var onExpire = ParseOutcomeActions(action.Config, "onExpire", context);

            if (context.IsDryRun)
            {
                Logger.LogInformation("[DRY-RUN] Would create approval request with {Count} approvers",
                    approvers.Count);

                return Success(new Dictionary<string, object>
                {
                    ["dryRun"] = true,
                    ["approvers"] = approversList,
                    ["approvalType"] = approvalType.ToString()
                });
            }

            var request = new ApprovalRequest
            {
                Id = Guid.NewGuid(),
                ContentId = context.Content.Id,
                ContentType = context.Content.ContentType,
                WorkflowId = context.Workflow.Id,
                ActionId = action.Id,
                RequestedBy = context.User?.Id ?? Guid.Empty,
                RequestedAt = DateTime.UtcNow,
                Title = title,
                Description = description,
                Approvers = approvers,
                ApprovalType = approvalType,
                ApprovalThreshold = approvalType == ApprovalType.Threshold ? threshold : null,
                Status = ApprovalStatus.Pending,
                ExpiresAt = expiresAt,
                ReminderAt = reminderAt,
                OnApprove = onApprove,
                OnReject = onReject,
                OnExpire = onExpire,
                Token = GenerateSecureToken()
            };

            await _approvalService.CreateApprovalAsync(request, context.CancellationToken);

            // Store approval link in context variables
            var baseUrl = context.HttpContext != null
                ? $"{context.HttpContext.Request.Scheme}://{context.HttpContext.Request.Host}"
                : "http://localhost";

            context.Variables["approvalRequestId"] = request.Id;
            context.Variables["approvalLink"] = $"{baseUrl}/api/approvals/{request.Id}?token={request.Token}";

            Logger.LogInformation("Created approval request {RequestId} for content {ContentId}",
                request.Id, context.Content.Id);

            var result = Success(new Dictionary<string, object>
            {
                ["approvalRequestId"] = request.Id,
                ["approvalLink"] = context.Variables["approvalLink"],
                ["approvers"] = approversList
            });
            result.ApprovalRequestId = request.Id;
            return result;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to start approval");
            return Failure($"Failed to start approval: {ex.Message}");
        }
    }

    private Approver ParseApprover(string spec, int order)
    {
        // Format: "user:uuid", "role:RoleName", "email:address@example.com", or just email/userId
        var approver = new Approver { Order = order };

        if (spec.StartsWith("user:", StringComparison.OrdinalIgnoreCase))
        {
            if (Guid.TryParse(spec.Substring(5), out var userId))
                approver.UserId = userId;
            approver.Name = spec.Substring(5);
        }
        else if (spec.StartsWith("role:", StringComparison.OrdinalIgnoreCase))
        {
            approver.Role = spec.Substring(5);
            approver.Name = $"Role: {approver.Role}";
        }
        else if (spec.StartsWith("email:", StringComparison.OrdinalIgnoreCase))
        {
            approver.Email = spec.Substring(6);
            approver.Name = approver.Email;
        }
        else if (spec.Contains('@'))
        {
            approver.Email = spec;
            approver.Name = spec;
        }
        else if (Guid.TryParse(spec, out var userId))
        {
            approver.UserId = userId;
            approver.Name = userId.ToString();
        }
        else
        {
            // Assume it's a role name
            approver.Role = spec;
            approver.Name = $"Role: {spec}";
        }

        return approver;
    }

    private ApprovalOutcomeActions ParseOutcomeActions(
        Dictionary<string, object> config,
        string key,
        WorkflowContext context)
    {
        var actions = new ApprovalOutcomeActions();
        var dict = GetDictionary(config, key);

        if (dict.Count == 0)
            return actions;

        if (dict.TryGetValue("setStatus", out var status))
            actions.SetStatus = ResolveTemplateVariables(status?.ToString() ?? "", context);

        if (dict.TryGetValue("setFields", out var fields) && fields is Dictionary<string, object> fieldsDict)
            actions.SetFields = fieldsDict;

        if (dict.TryGetValue("triggerWorkflow", out var workflow) &&
            Guid.TryParse(workflow?.ToString(), out var workflowId))
            actions.TriggerWorkflow = workflowId;

        if (dict.TryGetValue("notifyRequestor", out var notify))
            actions.NotifyRequestor = notify?.ToString()?.ToLowerInvariant() == "true";

        if (dict.TryGetValue("emailTemplate", out var template))
            actions.EmailTemplate = template?.ToString();

        return actions;
    }

    private DateTime? ParseDuration(string duration)
    {
        if (string.IsNullOrEmpty(duration))
            return null;

        var now = DateTime.UtcNow;

        // Parse formats like "48h", "2d", "30m"
        var value = duration.TrimEnd('h', 'd', 'm', 's', 'H', 'D', 'M', 'S');
        if (!int.TryParse(value, out var amount))
            return null;

        var unit = duration.Last().ToString().ToLowerInvariant();

        return unit switch
        {
            "h" => now.AddHours(amount),
            "d" => now.AddDays(amount),
            "m" => now.AddMinutes(amount),
            "s" => now.AddSeconds(amount),
            _ => now.AddHours(amount)
        };
    }

    private string GenerateSecureToken()
    {
        var bytes = new byte[32];
        using var rng = System.Security.Cryptography.RandomNumberGenerator.Create();
        rng.GetBytes(bytes);
        return Convert.ToBase64String(bytes)
            .Replace("+", "-")
            .Replace("/", "_")
            .TrimEnd('=');
    }

    public override List<string> ValidateConfig(Dictionary<string, object> config)
    {
        var errors = new List<string>();

        if (!config.ContainsKey("approvers"))
            errors.Add("'approvers' is required.");

        return errors;
    }

    public override ActionConfigSchema GetConfigSchema()
    {
        return new ActionConfigSchema
        {
            Type = Type,
            Properties = new List<ActionConfigProperty>
            {
                new() { Name = "approvers", Type = "array", Description = "List of approvers (user:id, role:name, or email)", Required = true },
                new() { Name = "title", Type = "string", Description = "Approval request title" },
                new() { Name = "description", Type = "string", Description = "Approval request description" },
                new() { Name = "approvalType", Type = "string", Description = "any, all, sequential, or threshold", Enum = new List<string> { "any", "all", "sequential", "threshold" } },
                new() { Name = "threshold", Type = "integer", Description = "Number of approvals needed (for threshold type)" },
                new() { Name = "expiresIn", Type = "string", Description = "Expiration duration (e.g., 48h, 2d)" },
                new() { Name = "reminderAfter", Type = "string", Description = "Send reminder after duration" },
                new() { Name = "onApprove", Type = "object", Description = "Actions when approved" },
                new() { Name = "onReject", Type = "object", Description = "Actions when rejected" },
                new() { Name = "onExpire", Type = "object", Description = "Actions when expired" }
            },
            Required = new List<string> { "approvers" },
            Example = @"{""approvers"": [""role:Manager"", ""{{data.supervisorEmail}}""], ""approvalType"": ""all"", ""expiresIn"": ""48h""}"
        };
    }
}
