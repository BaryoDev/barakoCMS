using barakoCMS.Features.WorkflowsV2.Models;
using barakoCMS.Features.WorkflowsV2.Queue;
using Marten;
using Microsoft.Extensions.Logging;

namespace barakoCMS.Features.WorkflowsV2.Services;

/// <summary>
/// Enhanced workflow engine with pre/post hooks and async execution.
/// </summary>
public class WorkflowEngineV2 : IWorkflowEngineV2
{
    private readonly IDocumentSession _session;
    private readonly IActionRegistry _actionRegistry;
    private readonly IAdvancedConditionEvaluator _conditionEvaluator;
    private readonly IWorkflowQueueService _queueService;
    private readonly ILogger<WorkflowEngineV2> _logger;
    private readonly IServiceProvider _serviceProvider;

    public WorkflowEngineV2(
        IDocumentSession session,
        IActionRegistry actionRegistry,
        IAdvancedConditionEvaluator conditionEvaluator,
        IWorkflowQueueService queueService,
        IServiceProvider serviceProvider,
        ILogger<WorkflowEngineV2> logger)
    {
        _session = session;
        _actionRegistry = actionRegistry;
        _conditionEvaluator = conditionEvaluator;
        _queueService = queueService;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task<PreHookResult> ExecutePreHooksAsync(
        string contentType,
        string operation,
        barakoCMS.Models.Content content,
        barakoCMS.Models.User? user,
        CancellationToken ct)
    {
        var eventType = $"pre:{operation.ToLowerInvariant()}";
        var result = new PreHookResult { Continue = true };

        var workflows = await FindMatchingWorkflowsAsync(contentType, eventType, ct);

        if (workflows.Count == 0)
        {
            return result;
        }

        _logger.LogDebug("Found {Count} pre-hook workflows for {ContentType}.{Event}",
            workflows.Count, contentType, eventType);

        foreach (var workflow in workflows.OrderByDescending(w => w.Priority))
        {
            var context = CreateContext(workflow, content, null, user, eventType, isDryRun: false);

            if (!_conditionEvaluator.Evaluate(workflow.Trigger.Conditions, context))
            {
                _logger.LogDebug("Workflow {WorkflowId} conditions not met, skipping", workflow.Id);
                continue;
            }

            var executionResult = await ExecuteWorkflowAsync(workflow, context, ct);

            // Check for blocking
            if (executionResult.Blocked)
            {
                result.Continue = false;
                result.ErrorMessage = executionResult.BlockMessage;
                result.ValidationErrors = executionResult.ValidationErrors;

                _logger.LogInformation("Pre-hook {WorkflowId} blocked operation: {Message}",
                    workflow.Id, result.ErrorMessage);

                return result;
            }

            // Merge modified data
            if (executionResult.ModifiedData != null)
            {
                result.ModifiedData ??= new Dictionary<string, object>();
                foreach (var kv in executionResult.ModifiedData)
                {
                    result.ModifiedData[kv.Key] = kv.Value;
                    content.Data[kv.Key] = kv.Value;
                }
            }
        }

        return result;
    }

    public async Task ExecutePostHooksAsync(
        string contentType,
        string operation,
        barakoCMS.Models.Content content,
        barakoCMS.Models.Content? previousContent,
        barakoCMS.Models.User? user,
        CancellationToken ct)
    {
        var eventType = $"post:{operation.ToLowerInvariant()}";

        var workflows = await FindMatchingWorkflowsAsync(contentType, eventType, ct);

        if (workflows.Count == 0)
        {
            return;
        }

        _logger.LogDebug("Found {Count} post-hook workflows for {ContentType}.{Event}",
            workflows.Count, contentType, eventType);

        foreach (var workflow in workflows.OrderByDescending(w => w.Priority))
        {
            var context = CreateContext(workflow, content, previousContent, user, eventType, isDryRun: false);

            if (!_conditionEvaluator.Evaluate(workflow.Trigger.Conditions, context))
            {
                _logger.LogDebug("Workflow {WorkflowId} conditions not met, skipping", workflow.Id);
                continue;
            }

            // Check if async execution is configured
            var executeAsync = ShouldExecuteAsync(workflow);

            if (executeAsync)
            {
                await QueueWorkflowAsync(workflow, content, previousContent, user, eventType, ct);
            }
            else
            {
                await ExecuteWorkflowAsync(workflow, context, ct);
            }
        }
    }

    public async Task ExecuteWorkflowAsync(Guid workflowId, WorkflowQueueMessage message, CancellationToken ct)
    {
        var workflow = await _session.LoadAsync<WorkflowDefinitionV2>(workflowId, ct);

        if (workflow == null)
        {
            _logger.LogWarning("Workflow {WorkflowId} not found", workflowId);
            return;
        }

        var content = new barakoCMS.Models.Content
        {
            Id = message.ContentId,
            ContentType = message.ContentType,
            Data = message.ContentData,
            Status = Enum.TryParse<barakoCMS.Models.ContentStatus>(message.ContentStatus, out var status)
                ? status
                : barakoCMS.Models.ContentStatus.Draft
        };

        barakoCMS.Models.Content? previousContent = null;
        if (message.PreviousContentData != null)
        {
            previousContent = new barakoCMS.Models.Content
            {
                Id = message.ContentId,
                ContentType = message.ContentType,
                Data = message.PreviousContentData
            };
        }

        barakoCMS.Models.User? user = null;
        if (message.UserId.HasValue)
        {
            user = await _session.LoadAsync<barakoCMS.Models.User>(message.UserId.Value, ct);
        }

        var context = CreateContext(workflow, content, previousContent, user, message.TriggerEvent, isDryRun: false);
        context.CorrelationId = message.CorrelationId;

        await ExecuteWorkflowAsync(workflow, context, ct);
    }

    public async Task<WorkflowExecutionResult> DryRunAsync(
        WorkflowDefinitionV2 workflow,
        barakoCMS.Models.Content testContent,
        barakoCMS.Models.User? user,
        CancellationToken ct)
    {
        var context = CreateContext(workflow, testContent, null, user, workflow.Trigger.Event, isDryRun: true);
        return await ExecuteWorkflowAsync(workflow, context, ct);
    }

    private async Task<List<WorkflowDefinitionV2>> FindMatchingWorkflowsAsync(
        string contentType,
        string eventType,
        CancellationToken ct)
    {
        var workflows = await _session.Query<WorkflowDefinitionV2>()
            .Where(w => w.Enabled &&
                (w.Trigger.ContentType == contentType || w.Trigger.ContentType == "*") &&
                w.Trigger.Event == eventType)
            .ToListAsync(ct);
        return workflows.ToList();
    }

    private WorkflowContext CreateContext(
        WorkflowDefinitionV2 workflow,
        barakoCMS.Models.Content content,
        barakoCMS.Models.Content? previousContent,
        barakoCMS.Models.User? user,
        string triggerEvent,
        bool isDryRun)
    {
        return new WorkflowContext
        {
            Workflow = workflow,
            Content = content,
            PreviousContent = previousContent,
            User = user,
            TriggerEvent = triggerEvent,
            IsDryRun = isDryRun,
            ServiceProvider = _serviceProvider,
            ExecutionLog = new WorkflowExecutionLogV2
            {
                Id = Guid.NewGuid(),
                WorkflowId = workflow.Id,
                WorkflowName = workflow.Name,
                WorkflowVersion = workflow.Version,
                ContentId = content.Id,
                ContentType = content.ContentType,
                TriggerEvent = triggerEvent,
                TriggeredBy = user?.Id,
                StartedAt = DateTime.UtcNow,
                IsDryRun = isDryRun
            }
        };
    }

    private async Task<WorkflowExecutionResult> ExecuteWorkflowAsync(
        WorkflowDefinitionV2 workflow,
        WorkflowContext context,
        CancellationToken ct)
    {
        var result = new WorkflowExecutionResult();
        context.CancellationToken = ct;

        _logger.LogInformation("Executing workflow {WorkflowId} '{WorkflowName}' for content {ContentId}",
            workflow.Id, workflow.Name, context.Content.Id);

        try
        {
            foreach (var action in workflow.Actions)
            {
                ct.ThrowIfCancellationRequested();

                var actionLog = new ActionExecutionLogV2
                {
                    ActionId = action.Id,
                    ActionType = action.Type,
                    ActionName = action.Name,
                    StartedAt = DateTime.UtcNow
                };

                // Check action condition
                if (action.RunIf != null && !_conditionEvaluator.Evaluate(action.RunIf, context))
                {
                    actionLog.Status = ActionExecutionStatus.Skipped;
                    actionLog.WasSkipped = true;
                    actionLog.SkipReason = "Condition not met";
                    actionLog.CompletedAt = DateTime.UtcNow;
                    context.ExecutionLog.Actions.Add(actionLog);

                    _logger.LogDebug("Action {ActionId} skipped: condition not met", action.Id);
                    continue;
                }

                var actionHandler = _actionRegistry.GetAction(action.Type);
                if (actionHandler == null)
                {
                    _logger.LogWarning("Action type '{Type}' not found", action.Type);
                    actionLog.Status = ActionExecutionStatus.Skipped;
                    actionLog.WasSkipped = true;
                    actionLog.SkipReason = $"Action type '{action.Type}' not found";
                    actionLog.CompletedAt = DateTime.UtcNow;
                    context.ExecutionLog.Actions.Add(actionLog);
                    continue;
                }

                // Execute with retry logic
                var actionResult = await ExecuteActionWithRetryAsync(
                    actionHandler, action, context, actionLog, ct);

                context.ActionResults[action.Id] = actionResult;
                context.ExecutionLog.Actions.Add(actionLog);

                // Handle blocking (pre-hooks)
                if (actionResult.BlockOperation)
                {
                    result.Blocked = true;
                    result.BlockMessage = actionResult.BlockMessage;
                    result.ValidationErrors = actionResult.Output.TryGetValue("errors", out var errors)
                        ? errors as List<ValidationError>
                        : null;

                    _logger.LogInformation("Workflow blocked by action {ActionId}: {Message}",
                        action.Id, actionResult.BlockMessage);
                    break;
                }

                // Handle data modification
                if (actionResult.ModifiedData != null)
                {
                    result.ModifiedData ??= new Dictionary<string, object>();
                    foreach (var kv in actionResult.ModifiedData)
                    {
                        result.ModifiedData[kv.Key] = kv.Value;
                    }
                }

                // Handle failure
                if (!actionResult.Success)
                {
                    if (action.ContinueOnError || workflow.ErrorHandling.OnActionFailure == "continue")
                    {
                        _logger.LogWarning("Action {ActionId} failed but continuing: {Error}",
                            action.Id, actionResult.ErrorMessage);
                    }
                    else
                    {
                        result.Success = false;
                        result.ErrorMessage = actionResult.ErrorMessage;

                        _logger.LogError("Action {ActionId} failed, stopping workflow: {Error}",
                            action.Id, actionResult.ErrorMessage);
                        break;
                    }
                }

                // Handle workflow stop
                if (actionResult.StopWorkflow)
                {
                    _logger.LogInformation("Workflow stopped by action {ActionId}", action.Id);
                    break;
                }
            }

            // Complete execution log
            context.ExecutionLog.CompletedAt = DateTime.UtcNow;
            context.ExecutionLog.Duration = context.ExecutionLog.CompletedAt.Value - context.ExecutionLog.StartedAt;
            context.ExecutionLog.Status = result.Success
                ? WorkflowExecutionStatus.Completed
                : (result.Blocked ? WorkflowExecutionStatus.Cancelled : WorkflowExecutionStatus.Failed);

            // Save execution log
            if (!context.IsDryRun)
            {
                _session.Store(context.ExecutionLog);
                await _session.SaveChangesAsync(ct);
            }

            result.ExecutionLog = context.ExecutionLog;
            result.Success = !result.Blocked && result.ErrorMessage == null;
        }
        catch (OperationCanceledException)
        {
            context.ExecutionLog.Status = WorkflowExecutionStatus.Cancelled;
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Workflow {WorkflowId} failed with exception", workflow.Id);
            result.Success = false;
            result.ErrorMessage = ex.Message;

            context.ExecutionLog.Status = WorkflowExecutionStatus.Failed;
            context.ExecutionLog.ErrorMessage = ex.Message;
            context.ExecutionLog.ErrorStackTrace = ex.StackTrace;
        }

        return result;
    }

    private async Task<ActionResult> ExecuteActionWithRetryAsync(
        Actions.IWorkflowActionV2 handler,
        WorkflowActionV2 action,
        WorkflowContext context,
        ActionExecutionLogV2 actionLog,
        CancellationToken ct)
    {
        var maxRetries = action.RetryCount;
        var retryDelay = TimeSpan.FromSeconds(action.RetryDelaySeconds);
        var attempt = 0;

        while (true)
        {
            attempt++;
            actionLog.RetryAttempt = attempt - 1;
            actionLog.Status = ActionExecutionStatus.Running;

            try
            {
                using var cts = action.TimeoutSeconds > 0
                    ? new CancellationTokenSource(TimeSpan.FromSeconds(action.TimeoutSeconds))
                    : new CancellationTokenSource();

                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, cts.Token);

                var result = await handler.ExecuteAsync(action, context);

                actionLog.CompletedAt = DateTime.UtcNow;
                actionLog.Duration = actionLog.CompletedAt.Value - actionLog.StartedAt;
                actionLog.Status = result.Success ? ActionExecutionStatus.Completed : ActionExecutionStatus.Failed;
                actionLog.Output = result.Output;
                actionLog.ErrorMessage = result.ErrorMessage;

                if (result.Success || attempt > maxRetries)
                {
                    return result;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                actionLog.Status = ActionExecutionStatus.TimedOut;
                actionLog.ErrorMessage = "Action timed out";

                if (attempt > maxRetries)
                {
                    return new ActionResult
                    {
                        Success = false,
                        ErrorMessage = "Action timed out"
                    };
                }
            }
            catch (Exception ex)
            {
                actionLog.Status = ActionExecutionStatus.Failed;
                actionLog.ErrorMessage = ex.Message;
                actionLog.ErrorDetails = ex.StackTrace;

                if (attempt > maxRetries)
                {
                    return new ActionResult
                    {
                        Success = false,
                        ErrorMessage = ex.Message
                    };
                }
            }

            // Retry delay
            actionLog.Status = ActionExecutionStatus.Retrying;
            _logger.LogWarning("Action {ActionId} failed, retrying in {Delay}s (attempt {Attempt}/{MaxRetries})",
                action.Id, retryDelay.TotalSeconds, attempt, maxRetries + 1);

            await Task.Delay(retryDelay, ct);
        }
    }

    private bool ShouldExecuteAsync(WorkflowDefinitionV2 workflow)
    {
        // Could be configured via workflow settings
        // For now, check if any action is marked for async
        return workflow.Actions.Any(a =>
            a.Config.TryGetValue("async", out var async) &&
            (async?.ToString()?.ToLowerInvariant() == "true"));
    }

    private async Task QueueWorkflowAsync(
        WorkflowDefinitionV2 workflow,
        barakoCMS.Models.Content content,
        barakoCMS.Models.Content? previousContent,
        barakoCMS.Models.User? user,
        string triggerEvent,
        CancellationToken ct)
    {
        var message = new WorkflowQueueMessage
        {
            WorkflowId = workflow.Id,
            ContentId = content.Id,
            ContentType = content.ContentType,
            ContentData = content.Data,
            ContentStatus = content.Status.ToString(),
            PreviousContentData = previousContent?.Data,
            TriggerEvent = triggerEvent,
            UserId = user?.Id,
            Priority = workflow.Priority
        };

        await _queueService.PublishAsync(message, ct);

        _logger.LogInformation("Queued workflow {WorkflowId} for async execution", workflow.Id);
    }
}

/// <summary>
/// Result of workflow execution.
/// </summary>
public class WorkflowExecutionResult
{
    public bool Success { get; set; } = true;
    public bool Blocked { get; set; }
    public string? BlockMessage { get; set; }
    public string? ErrorMessage { get; set; }
    public Dictionary<string, object>? ModifiedData { get; set; }
    public List<ValidationError>? ValidationErrors { get; set; }
    public WorkflowExecutionLogV2? ExecutionLog { get; set; }
}

/// <summary>
/// Interface for the enhanced workflow engine.
/// </summary>
public interface IWorkflowEngineV2
{
    /// <summary>
    /// Execute pre-operation hooks. Returns whether to continue with the operation.
    /// </summary>
    Task<PreHookResult> ExecutePreHooksAsync(
        string contentType,
        string operation,
        barakoCMS.Models.Content content,
        barakoCMS.Models.User? user,
        CancellationToken ct);

    /// <summary>
    /// Execute post-operation hooks.
    /// </summary>
    Task ExecutePostHooksAsync(
        string contentType,
        string operation,
        barakoCMS.Models.Content content,
        barakoCMS.Models.Content? previousContent,
        barakoCMS.Models.User? user,
        CancellationToken ct);

    /// <summary>
    /// Execute a queued workflow.
    /// </summary>
    Task ExecuteWorkflowAsync(Guid workflowId, WorkflowQueueMessage message, CancellationToken ct);

    /// <summary>
    /// Dry-run a workflow for testing.
    /// </summary>
    Task<WorkflowExecutionResult> DryRunAsync(
        WorkflowDefinitionV2 workflow,
        barakoCMS.Models.Content testContent,
        barakoCMS.Models.User? user,
        CancellationToken ct);
}
