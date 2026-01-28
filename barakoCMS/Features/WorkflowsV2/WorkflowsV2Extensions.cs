using barakoCMS.Features.WorkflowsV2.Actions;
using barakoCMS.Features.WorkflowsV2.Actions.ApprovalWorkflow;
using barakoCMS.Features.WorkflowsV2.Actions.Communication;
using barakoCMS.Features.WorkflowsV2.Actions.DataOperations;
using barakoCMS.Features.WorkflowsV2.Actions.ExternalIntegration;
using barakoCMS.Features.WorkflowsV2.Actions.LogicControl;
using barakoCMS.Features.WorkflowsV2.Actions.TaskManagement;
using barakoCMS.Features.WorkflowsV2.Actions.Validation;
using barakoCMS.Features.WorkflowsV2.Queue;
using barakoCMS.Features.WorkflowsV2.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace barakoCMS.Features.WorkflowsV2;

/// <summary>
/// Extension methods for registering WorkflowsV2 services.
/// </summary>
public static class WorkflowsV2Extensions
{
    /// <summary>
    /// Adds WorkflowsV2 services to the service collection.
    /// </summary>
    public static IServiceCollection AddWorkflowsV2(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Configuration
        services.Configure<RabbitMQSettings>(configuration.GetSection("WorkflowsV2:RabbitMQ"));
        services.Configure<SmtpSettings>(configuration.GetSection("WorkflowsV2:Smtp"));
        services.Configure<CredentialEncryptionSettings>(configuration.GetSection("WorkflowsV2:Credentials"));

        // Core services
        services.AddScoped<IAdvancedConditionEvaluator, AdvancedConditionEvaluator>();
        services.AddScoped<IWorkflowVersionService, WorkflowVersionService>();
        services.AddScoped<IWorkflowExportService, WorkflowExportService>();
        services.AddScoped<ICredentialService, CredentialService>();
        services.AddScoped<IApprovalService, ApprovalService>();

        // Email services
        var templatesPath = configuration.GetValue<string>("WorkflowsV2:EmailTemplatesPath")
            ?? Path.Combine(AppContext.BaseDirectory, "email-templates");
        services.AddSingleton<IEmailTemplateService>(sp =>
            new EmailTemplateService(templatesPath, sp.GetRequiredService<ILogger<EmailTemplateService>>()));
        services.AddSingleton<IEmailSenderService, EmailSenderService>();

        // SMS service (no-op by default, can be replaced)
        services.AddSingleton<Actions.Communication.ISmsService, Actions.Communication.NoOpSmsService>();

        // Queue service
        services.AddSingleton<IWorkflowQueueService, WorkflowQueueService>();

        // Action registry (singleton)
        services.AddSingleton<IActionRegistry, ActionRegistry>();

        // Register all actions
        RegisterActions(services);

        // Workflow engine (scoped for per-request)
        services.AddScoped<IWorkflowEngineV2, WorkflowEngineV2>();

        // Background services
        services.AddHostedService<WorkflowQueueConsumer>();
        services.AddHostedService<ApprovalExpirationService>();

        return services;
    }

    private static void RegisterActions(IServiceCollection services)
    {
        // Communication actions
        services.AddScoped<IWorkflowActionV2, SendEmailAction>();
        services.AddScoped<IWorkflowActionV2, SendSmsAction>();

        // Data operations
        services.AddScoped<IWorkflowActionV2, SetFieldAction>();
        services.AddScoped<IWorkflowActionV2, TransformDataAction>();

        // External integration
        services.AddScoped<IWorkflowActionV2, CallWebhookAction>();

        // Logic control
        services.AddScoped<IWorkflowActionV2, ConditionAction>();
        services.AddScoped<IWorkflowActionV2, LogAction>();
        services.AddScoped<IWorkflowActionV2, DelayAction>();
        services.AddScoped<IWorkflowActionV2, ForEachAction>();

        // Approval workflow
        services.AddScoped<IWorkflowActionV2, StartApprovalAction>();

        // Task management
        services.AddScoped<IWorkflowActionV2, CreateTaskAction>();

        // Validation
        services.AddScoped<IWorkflowActionV2, ValidateFieldAction>();
    }
}

/// <summary>
/// Background service that consumes workflow messages from the queue.
/// </summary>
public class WorkflowQueueConsumer : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IWorkflowQueueService _queueService;
    private readonly ILogger<WorkflowQueueConsumer> _logger;

    public WorkflowQueueConsumer(
        IServiceProvider serviceProvider,
        IWorkflowQueueService queueService,
        ILogger<WorkflowQueueConsumer> logger)
    {
        _serviceProvider = serviceProvider;
        _queueService = queueService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Workflow queue consumer starting");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var message = await _queueService.ConsumeAsync(stoppingToken);

                if (message != null)
                {
                    await ProcessMessageAsync(message, stoppingToken);
                }
                else
                {
                    // No message available, wait before polling again
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Shutdown requested
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in workflow queue consumer");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }

        _logger.LogInformation("Workflow queue consumer stopped");
    }

    private async Task ProcessMessageAsync(WorkflowQueueMessage message, CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<IWorkflowEngineV2>();

        try
        {
            _logger.LogInformation("Processing workflow message {MessageId} for workflow {WorkflowId}",
                message.MessageId, message.WorkflowId);

            // Execute the workflow using the queue message
            await engine.ExecuteWorkflowAsync(message.WorkflowId, message, ct);

            await _queueService.AcknowledgeAsync(message, ct);
            _logger.LogInformation("Workflow {WorkflowId} executed successfully", message.WorkflowId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing workflow message {MessageId}", message.MessageId);

            message.RetryCount++;

            if (message.RetryCount < message.MaxRetries)
            {
                await _queueService.RejectAsync(message, requeue: true, ct);
            }
            else
            {
                await _queueService.PublishToDeadLetterAsync(new WorkflowDeadLetterMessage
                {
                    OriginalMessage = message,
                    FailedAt = DateTime.UtcNow,
                    ErrorMessage = ex.Message,
                    StackTrace = ex.StackTrace,
                    AttemptCount = message.RetryCount
                }, ct);

                await _queueService.AcknowledgeAsync(message, ct);
            }
        }
    }
}

/// <summary>
/// Background service that expires overdue approval requests.
/// </summary>
public class ApprovalExpirationService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ApprovalExpirationService> _logger;
    private readonly TimeSpan _checkInterval = TimeSpan.FromMinutes(5);

    public ApprovalExpirationService(
        IServiceProvider serviceProvider,
        ILogger<ApprovalExpirationService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Approval expiration service starting");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var approvalService = scope.ServiceProvider.GetRequiredService<IApprovalService>();

                var expiredCount = await approvalService.ExpireOverdueApprovalsAsync(stoppingToken);

                if (expiredCount > 0)
                {
                    _logger.LogInformation("Expired {Count} overdue approval requests", expiredCount);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in approval expiration service");
            }

            await Task.Delay(_checkInterval, stoppingToken);
        }

        _logger.LogInformation("Approval expiration service stopped");
    }
}
