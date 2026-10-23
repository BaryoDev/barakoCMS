namespace barakoCMS.Models;

/// <summary>Where an <see cref="OutboxMessage"/> is in its delivery.</summary>
public enum OutboxStatus
{
    /// <summary>Waiting to be sent, or waiting for its next attempt.</summary>
    Pending = 0,

    /// <summary>The receiver answered with a success status.</summary>
    Delivered = 1,

    /// <summary>Given up on: a refusal that a retry cannot change, or the attempts ran out.</summary>
    DeadLettered = 2,
}

/// <summary>
/// A message a workflow's Publish action left for an outside service, kept until it is delivered.
/// </summary>
/// <remarks>
/// Written in the same transaction that records the action as succeeded, so the run says "published"
/// only when this row exists, and a run that is rolled back leaves no message behind. A sender sends
/// it afterwards and retries with backoff, so an outage on the receiving side delays the message and
/// does not lose it. <see cref="Id"/> is sent as the message id and is stable across retries, which
/// is what lets a receiver drop a duplicate.
/// </remarks>
public class OutboxMessage
{
    /// <summary>The message id, and the receiver's dedupe key.</summary>
    public Guid Id { get; set; }

    public string Tenant { get; set; } = string.Empty;

    public Guid? RunId { get; set; }

    public Guid? WorkflowId { get; set; }

    public string ContentType { get; set; } = string.Empty;

    public Guid ContentId { get; set; }

    public string Topic { get; set; } = string.Empty;

    public string MessageType { get; set; } = string.Empty;

    /// <summary>Where it goes. Read only by the sender; never served over the API.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>The signing secret as the workflow stores it, encrypted. Null sends unsigned.</summary>
    public string? Secret { get; set; }

    /// <summary>The exact JSON that is sent and signed.</summary>
    public string Body { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public OutboxStatus Status { get; set; } = OutboxStatus.Pending;

    /// <summary>How many sends have been tried.</summary>
    public int Attempts { get; set; }

    /// <summary>The earliest the next send may start.</summary>
    public DateTimeOffset NextAttemptAt { get; set; }

    /// <summary>Held by a sender until then, so two nodes do not send the same message.</summary>
    public DateTimeOffset? LeasedUntil { get; set; }

    public DateTimeOffset? DeliveredAt { get; set; }

    /// <summary>Why the last send failed. Never a response body.</summary>
    public string? LastError { get; set; }
}
