using barakoCMS.Features.WorkflowsV2.Models;
using Marten;
using Microsoft.Extensions.Logging;

namespace barakoCMS.Features.WorkflowsV2.Actions.TaskManagement;

/// <summary>
/// Action that creates a task/todo item.
/// </summary>
public class CreateTaskAction : BaseWorkflowAction
{
    private readonly IDocumentSession _session;

    public override string Type => "CreateTask";
    public override string Name => "Create Task";
    public override string Category => ActionCategories.ApprovalWorkflow;
    public override string Description => "Create a task or todo item";

    public CreateTaskAction(IDocumentSession session, ILogger<CreateTaskAction> logger) : base(logger)
    {
        _session = session;
    }

    public override async Task<ActionResult> ExecuteAsync(WorkflowActionV2 action, WorkflowContext context)
    {
        var title = GetRequiredString(action.Config, "title", context);
        var description = GetString(action.Config, "description", context, "");
        var priority = GetString(action.Config, "priority", context, "medium");
        var assigneeId = GetString(action.Config, "assigneeId", context, "");
        var dueInDays = GetInt(action.Config, "dueInDays", 0);
        var tags = GetStringList(action.Config, "tags", context);

        if (string.IsNullOrEmpty(title))
        {
            return Failure("Task title is required");
        }

        if (context.IsDryRun)
        {
            Logger.LogInformation("[DRY-RUN] Would create task: {Title}", title);
            return Success(new Dictionary<string, object>
            {
                ["dryRun"] = true,
                ["title"] = title
            });
        }

        var task = new WorkflowTask
        {
            Id = Guid.NewGuid(),
            Title = title,
            Description = description,
            Priority = ParsePriority(priority),
            Status = WorkflowTaskStatus.Open,
            ContentType = context.Content.ContentType,
            ContentId = context.Content.Id,
            WorkflowId = context.Workflow.Id,
            CreatedAt = DateTime.UtcNow,
            CreatedBy = context.User?.Id ?? Guid.Empty,
            Tags = tags
        };

        if (!string.IsNullOrEmpty(assigneeId) && Guid.TryParse(assigneeId, out var assignee))
        {
            task.AssigneeId = assignee;
        }

        if (dueInDays > 0)
        {
            task.DueDate = DateTime.UtcNow.AddDays(dueInDays);
        }

        _session.Store(task);
        await _session.SaveChangesAsync(context.CancellationToken);

        Logger.LogInformation("Created task {TaskId}: {Title}", task.Id, title);

        return Success(new Dictionary<string, object>
        {
            ["taskId"] = task.Id.ToString(),
            ["title"] = title,
            ["status"] = task.Status.ToString()
        });
    }

    private WorkflowTaskPriority ParsePriority(string priority)
    {
        return priority.ToLowerInvariant() switch
        {
            "low" => WorkflowTaskPriority.Low,
            "high" => WorkflowTaskPriority.High,
            "urgent" => WorkflowTaskPriority.Urgent,
            _ => WorkflowTaskPriority.Medium
        };
    }

    public override List<string> ValidateConfig(Dictionary<string, object> config)
    {
        var errors = new List<string>();

        if (!config.ContainsKey("title"))
            errors.Add("'title' is required");

        return errors;
    }

    public override ActionConfigSchema GetConfigSchema()
    {
        return new ActionConfigSchema
        {
            Type = Type,
            Properties = new List<ActionConfigProperty>
            {
                new() { Name = "title", Type = "string", Description = "Task title", Required = true },
                new() { Name = "description", Type = "string", Description = "Task description" },
                new() { Name = "priority", Type = "string", Description = "Priority level", Enum = new List<string> { "low", "medium", "high", "urgent" } },
                new() { Name = "assigneeId", Type = "string", Description = "User ID to assign the task to" },
                new() { Name = "dueInDays", Type = "integer", Description = "Number of days until task is due" },
                new() { Name = "tags", Type = "array", Description = "Tags to apply to the task" }
            },
            Required = new List<string> { "title" },
            Example = @"{""title"": ""Review {{data.name}}"", ""priority"": ""high"", ""dueInDays"": 3}"
        };
    }
}

/// <summary>
/// Workflow task entity.
/// </summary>
public class WorkflowTask
{
    public Guid Id { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public WorkflowTaskPriority Priority { get; set; } = WorkflowTaskPriority.Medium;
    public WorkflowTaskStatus Status { get; set; } = WorkflowTaskStatus.Open;
    public string ContentType { get; set; } = "";
    public Guid? ContentId { get; set; }
    public Guid WorkflowId { get; set; }
    public Guid? AssigneeId { get; set; }
    public DateTime? DueDate { get; set; }
    public DateTime CreatedAt { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTime? CompletedAt { get; set; }
    public Guid? CompletedBy { get; set; }
    public List<string> Tags { get; set; } = new();
}

public enum WorkflowTaskPriority { Low, Medium, High, Urgent }
public enum WorkflowTaskStatus { Open, InProgress, Completed, Cancelled }
