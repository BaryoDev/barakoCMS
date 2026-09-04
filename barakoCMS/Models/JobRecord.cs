using System.Text.Json;
using System.Text.Json.Serialization;
using FastEndpoints;
using JasperFx;
using Marten.Schema;

namespace barakoCMS.Models;

/// <summary>Where a queued job is in its life.</summary>
public enum JobState
{
    /// <summary>Stored and waiting for <see cref="JobRecord.ExecuteAfter"/>.</summary>
    Pending = 0,

    /// <summary>Claimed by a worker. The lease is <see cref="JobRecord.DequeueAfter"/>.</summary>
    Running = 1,

    /// <summary>The handler returned without throwing.</summary>
    Completed = 2,

    /// <summary>
    /// Given up on: the handler failed <see cref="JobRecord.MaxAttempts"/> times, the job expired
    /// before it ran, or it was cancelled. Nothing picks it up again without an operator.
    /// </summary>
    DeadLettered = 3,
}

/// <summary>
/// One queued command, stored as a Marten document in the tenant of the request that queued it.
/// </summary>
/// <remarks>
/// The shape FastEndpoints needs (<see cref="IJobStorageRecord"/>) plus what the queue needs to own
/// retry: the attempt count, when the next attempt is due, the last error, and a state that can say
/// "gave up". Those are here from the first version because #106 decided the queue owns retry for
/// webhooks, email and indexing, and a record without them cannot be upgraded into one that has
/// them without a migration.
///
/// The command is stored as JSON in <see cref="CommandJson"/> rather than as a nested object. Marten
/// would happily serialise <see cref="Command"/> as an <c>object</c>, but reading it back gives a
/// <c>JsonElement</c>, not the command type, and FastEndpoints hands the record a generic
/// <see cref="GetCommand{TCommand}"/> to deserialise with. Keeping the text is what makes that
/// deterministic.
/// </remarks>
public sealed class JobRecord : IJobStorageRecord, IHasCommandType
{
    /// <summary>The job's id. FastEndpoints assigns it and reports it back from <c>QueueJobAsync</c>.</summary>
    [Identity]
    public Guid TrackingID { get; set; }

    /// <summary>
    /// The Marten tenant the job was queued in, mapped from the tenant column so a worker with no
    /// request can open a session for the right partition when it updates the record.
    /// </summary>
    public string TenantId { get; set; } = StorageConstants.DefaultTenantId;

    /// <summary>One queue per command type; FastEndpoints derives it from the type name.</summary>
    public string QueueID { get; set; } = string.Empty;

    /// <summary>The command's full type name, filled in by FastEndpoints.</summary>
    public string CommandType { get; set; } = string.Empty;

    public string CommandJson { get; set; } = string.Empty;

    /// <summary>The live command object while a job is being queued or executed. Not stored.</summary>
    [JsonIgnore]
    public object Command { get; set; } = default!;

    public DateTime CreatedAt { get; set; }

    public DateTime ExecuteAfter { get; set; }

    public DateTime ExpireOn { get; set; }

    /// <summary>
    /// The lease. A worker claiming the job moves this into the future, and another worker may take
    /// the job only once it has passed, so a crash mid-job frees it by time rather than by anybody
    /// noticing.
    /// </summary>
    public DateTime DequeueAfter { get; set; }

    public bool IsComplete { get; set; }

    public JobState State { get; set; } = JobState.Pending;

    /// <summary>How many times a handler has been run for this job and thrown.</summary>
    public int AttemptCount { get; set; }

    /// <summary>Copied from <c>Jobs:MaxAttempts</c> when the job is queued, so a config change does not move the goalposts on a job already in flight.</summary>
    public int MaxAttempts { get; set; }

    /// <summary>When the next attempt is due after a failure. Null when none is planned.</summary>
    public DateTime? NextAttemptAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    /// <summary>The last exception's type and message. Never a stack trace and never a response body.</summary>
    public string? LastError { get; set; }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.General);

    public TCommand GetCommand<TCommand>() where TCommand : class, ICommandBase =>
        JsonSerializer.Deserialize<TCommand>(CommandJson, Json)
        ?? throw new InvalidOperationException($"Job {TrackingID} holds no {typeof(TCommand).Name} to run.");

    public void SetCommand<TCommand>(TCommand command) where TCommand : class, ICommandBase
    {
        Command = command;
        CommandJson = JsonSerializer.Serialize(command, Json);
    }
}
