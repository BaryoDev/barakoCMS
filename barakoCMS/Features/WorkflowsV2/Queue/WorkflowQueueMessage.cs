namespace barakoCMS.Features.WorkflowsV2.Queue;

/// <summary>
/// Message sent to RabbitMQ for async workflow execution.
/// </summary>
public class WorkflowQueueMessage
{
    /// <summary>
    /// Unique message ID.
    /// </summary>
    public string MessageId { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Workflow to execute.
    /// </summary>
    public Guid WorkflowId { get; set; }

    /// <summary>
    /// Content ID that triggered the workflow.
    /// </summary>
    public Guid ContentId { get; set; }

    /// <summary>
    /// Content type.
    /// </summary>
    public string ContentType { get; set; } = string.Empty;

    /// <summary>
    /// Trigger event.
    /// </summary>
    public string TriggerEvent { get; set; } = string.Empty;

    /// <summary>
    /// User who triggered the event.
    /// </summary>
    public Guid? UserId { get; set; }

    /// <summary>
    /// Snapshot of content data at trigger time.
    /// </summary>
    public Dictionary<string, object> ContentData { get; set; } = new();

    /// <summary>
    /// Previous content data (for updates).
    /// </summary>
    public Dictionary<string, object>? PreviousContentData { get; set; }

    /// <summary>
    /// Content status.
    /// </summary>
    public string ContentStatus { get; set; } = string.Empty;

    /// <summary>
    /// Correlation ID for tracing.
    /// </summary>
    public string CorrelationId { get; set; } = string.Empty;

    /// <summary>
    /// When the message was created.
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Number of retry attempts.
    /// </summary>
    public int RetryCount { get; set; }

    /// <summary>
    /// Maximum retry attempts.
    /// </summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    /// Delay before next retry in seconds.
    /// </summary>
    public int RetryDelaySeconds { get; set; } = 60;

    /// <summary>
    /// Priority (higher = processed first).
    /// </summary>
    public int Priority { get; set; }

    /// <summary>
    /// Scheduled execution time (for delayed execution).
    /// </summary>
    public DateTime? ScheduledFor { get; set; }

    /// <summary>
    /// Additional metadata.
    /// </summary>
    public Dictionary<string, string> Metadata { get; set; } = new();
}

/// <summary>
/// Dead letter message for failed workflows.
/// </summary>
public class WorkflowDeadLetterMessage
{
    public Guid Id { get; set; }

    /// <summary>
    /// Original queue message.
    /// </summary>
    public WorkflowQueueMessage OriginalMessage { get; set; } = null!;

    /// <summary>
    /// Error that caused the failure.
    /// </summary>
    public string ErrorMessage { get; set; } = string.Empty;

    /// <summary>
    /// Stack trace.
    /// </summary>
    public string? StackTrace { get; set; }

    /// <summary>
    /// When the message was moved to dead letter.
    /// </summary>
    public DateTime FailedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Number of attempts made.
    /// </summary>
    public int AttemptCount { get; set; }

    /// <summary>
    /// Whether this has been manually processed.
    /// </summary>
    public bool IsProcessed { get; set; }

    /// <summary>
    /// Notes from manual processing.
    /// </summary>
    public string? ProcessingNotes { get; set; }

    /// <summary>
    /// Who processed this dead letter.
    /// </summary>
    public Guid? ProcessedBy { get; set; }

    /// <summary>
    /// When it was processed.
    /// </summary>
    public DateTime? ProcessedAt { get; set; }
}

/// <summary>
/// RabbitMQ configuration.
/// </summary>
public class RabbitMQSettings
{
    public string HostName { get; set; } = "localhost";
    public int Port { get; set; } = 5672;
    public string UserName { get; set; } = "guest";
    public string Password { get; set; } = "guest";
    public string VirtualHost { get; set; } = "/";

    /// <summary>
    /// Exchange for workflow messages.
    /// </summary>
    public string ExchangeName { get; set; } = "barakocms.workflows";

    /// <summary>
    /// Queue for workflow execution.
    /// </summary>
    public string QueueName { get; set; } = "barakocms.workflows.execute";

    /// <summary>
    /// Dead letter queue.
    /// </summary>
    public string DeadLetterQueueName { get; set; } = "barakocms.workflows.deadletter";

    /// <summary>
    /// Delay queue for scheduled/retry messages.
    /// </summary>
    public string DelayQueueName { get; set; } = "barakocms.workflows.delay";

    /// <summary>
    /// Number of concurrent consumers.
    /// </summary>
    public int ConsumerCount { get; set; } = 2;

    /// <summary>
    /// Prefetch count per consumer.
    /// </summary>
    public int PrefetchCount { get; set; } = 10;

    /// <summary>
    /// Message TTL in milliseconds.
    /// </summary>
    public int MessageTtlMs { get; set; } = 86400000; // 24 hours
}
