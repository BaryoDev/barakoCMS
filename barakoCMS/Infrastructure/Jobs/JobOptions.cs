using Microsoft.Extensions.Configuration;

namespace barakoCMS.Infrastructure.Jobs;

/// <summary>The job queue's retry, retention and measurement settings, read once at startup.</summary>
public sealed class JobOptions
{
    public const string MaxAttemptsKey = "Jobs:MaxAttempts";
    public const string BackoffBaseSecondsKey = "Jobs:BackoffBaseSeconds";
    public const string BackoffMaxSecondsKey = "Jobs:BackoffMaxSeconds";
    public const string StorageProbeSecondsKey = "Jobs:StorageProbeSeconds";
    public const string LeaseSecondsKey = "Jobs:LeaseSeconds";
    public const string DeadLetterRetentionDaysKey = "Jobs:DeadLetterRetentionDays";
    public const string MetricsIntervalSecondsKey = "Jobs:MetricsIntervalSeconds";

    public const int DefaultMaxAttempts = 5;
    public const int DefaultBackoffBaseSeconds = 30;
    public const int DefaultBackoffMaxSeconds = 3600;
    public const int DefaultStorageProbeSeconds = 60;
    public const int DefaultLeaseSeconds = 600;
    public const int DefaultDeadLetterRetentionDays = 90;
    public const int DefaultMetricsIntervalSeconds = 30;
    public const int MaxMetricsIntervalSeconds = 3600;

    /// <summary>How many times a handler may throw before the job is dead-lettered.</summary>
    public int MaxAttempts { get; init; } = DefaultMaxAttempts;

    /// <summary>
    /// The wait after the first failure. Each failure after that doubles it. Every wait is then
    /// shortened by a random share of up to a quarter, so jobs that failed together do not retry
    /// together.
    /// </summary>
    public int BackoffBaseSeconds { get; init; } = DefaultBackoffBaseSeconds;

    /// <summary>The longest wait between two attempts, whatever the doubling says. Jitter never passes it.</summary>
    public int BackoffMaxSeconds { get; init; } = DefaultBackoffMaxSeconds;

    /// <summary>
    /// How often a worker re-reads storage for jobs it was not told about: a retry that came due, or
    /// a job another instance queued. A committed enqueue wakes the worker on its own, so this is
    /// the latency of retries and of cross-instance pickup, not of an ordinary enqueue.
    /// </summary>
    public int StorageProbeSeconds { get; init; } = DefaultStorageProbeSeconds;

    /// <summary>
    /// How long a claimed job stays claimed, and how long a handler may run. Another instance may
    /// take the job once this has passed, so the handler's token is cancelled at the same moment
    /// and the attempt counts as a failure rather than running twice.
    /// </summary>
    public int LeaseSeconds { get; init; } = DefaultLeaseSeconds;

    /// <summary>
    /// How long a dead-lettered or cancelled job is kept after it gave up. Zero or less keeps it
    /// forever, which is what happened before the setting existed.
    /// </summary>
    /// <remarks>
    /// Ninety days, the window a failed workflow run is kept for. A dead letter is there for an
    /// operator to read, and a quarter is long enough to notice one and act on it, while the table no
    /// longer grows without bound once email and webhooks are jobs.
    /// </remarks>
    public int DeadLetterRetentionDays { get; init; } = DefaultDeadLetterRetentionDays;

    /// <summary>
    /// The least time between two counts of the due and dead-lettered jobs for the queue gauges.
    /// Zero switches the count off.
    /// </summary>
    public int MetricsIntervalSeconds { get; init; } = DefaultMetricsIntervalSeconds;

    public static JobOptions FromConfiguration(IConfiguration configuration) => new()
    {
        MaxAttempts = configuration.GetValue(MaxAttemptsKey, DefaultMaxAttempts),
        BackoffBaseSeconds = configuration.GetValue(BackoffBaseSecondsKey, DefaultBackoffBaseSeconds),
        BackoffMaxSeconds = configuration.GetValue(BackoffMaxSecondsKey, DefaultBackoffMaxSeconds),
        StorageProbeSeconds = configuration.GetValue(StorageProbeSecondsKey, DefaultStorageProbeSeconds),
        LeaseSeconds = configuration.GetValue(LeaseSecondsKey, DefaultLeaseSeconds),
        DeadLetterRetentionDays = configuration.GetValue(DeadLetterRetentionDaysKey, DefaultDeadLetterRetentionDays),
        MetricsIntervalSeconds = configuration.GetValue(MetricsIntervalSecondsKey, DefaultMetricsIntervalSeconds),
    };

    public void Validate()
    {
        if (MaxAttempts < 1)
            throw new InvalidOperationException($"{MaxAttemptsKey} must be at least 1; a job needs one attempt to run at all.");
        if (BackoffBaseSeconds < 0)
            throw new InvalidOperationException($"{BackoffBaseSecondsKey} cannot be negative.");
        if (BackoffMaxSeconds < BackoffBaseSeconds)
            throw new InvalidOperationException($"{BackoffMaxSecondsKey} must be at least {BackoffBaseSecondsKey}, or the cap cuts the first wait.");
        if (StorageProbeSeconds < 1)
            throw new InvalidOperationException($"{StorageProbeSecondsKey} must be at least 1.");
        if (LeaseSeconds < 1)
            throw new InvalidOperationException($"{LeaseSecondsKey} must be at least 1.");
        if (MetricsIntervalSeconds is < 0 or > MaxMetricsIntervalSeconds)
            throw new InvalidOperationException(
                $"{MetricsIntervalSecondsKey} must be between 0 and {MaxMetricsIntervalSeconds}; 0 switches the queue count off.");
    }
}
