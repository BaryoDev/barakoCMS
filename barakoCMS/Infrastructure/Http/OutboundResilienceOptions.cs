using barakoCMS.Features.Workflows;
using Microsoft.Extensions.Configuration;

namespace barakoCMS.Infrastructure.Http;

/// <summary>
/// The retry, timeout and breaker settings for one outbound call inside one workflow attempt, read
/// once at startup from <c>Workflows:Outbound</c>.
/// </summary>
/// <remarks>
/// This layer sits inside a single attempt. The durable queue still owns retry across processes
/// and days; this only saves a whole durable attempt from being spent on a reset socket or one 503
/// during a deploy.
///
/// The defaults keep what the outbound client did before: it already made up to four tries with a
/// ten second limit on each, through the standard resilience handler this replaces. Two extra tries
/// is one fewer, because that handler also retried a 500 and any POST after a timeout, and this one
/// does not.
/// </remarks>
internal sealed record OutboundResilienceOptions
{
    public const string Section = "Workflows:Outbound";
    public const string RetriesKey = Section + ":Retries";
    public const string AttemptTimeoutSecondsKey = Section + ":AttemptTimeoutSeconds";
    public const string EmailAttemptTimeoutSecondsKey = Section + ":EmailAttemptTimeoutSeconds";
    public const string BaseDelayMillisecondsKey = Section + ":BaseDelayMilliseconds";
    public const string MaxDelaySecondsKey = Section + ":MaxDelaySeconds";
    public const string MaxRetryAfterSecondsKey = Section + ":MaxRetryAfterSeconds";
    public const string BreakerFailuresKey = Section + ":BreakerFailures";
    public const string BreakerWindowKey = Section + ":BreakerWindow";
    public const string BreakerSamplingSecondsKey = Section + ":BreakerSamplingSeconds";
    public const string BreakerOpenSecondsKey = Section + ":BreakerOpenSeconds";

    public const int MaxRetries = 5;

    /// <summary>Tries after the first. Zero sends once.</summary>
    public int Retries { get; init; } = 2;

    /// <summary>How long one HTTP try may take to answer with headers.</summary>
    public TimeSpan AttemptTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long one email send may take. Longer than an HTTP try, because a send carries attachments
    /// and an SMTP session has several round trips.
    /// </summary>
    public TimeSpan EmailAttemptTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The floor of the decorrelated jitter between tries.</summary>
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>The ceiling of the jitter between tries.</summary>
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The longest <c>Retry-After</c> waited for. A provider asking for longer is not tried again
    /// inside this attempt; the durable queue's backoff is the right place for a long wait.
    /// </summary>
    public TimeSpan MaxRetryAfter { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Failed calls to one destination that open its breaker. Zero turns the breaker off.</summary>
    public int BreakerFailures { get; init; } = 5;

    /// <summary>How many recent calls the failures are counted over.</summary>
    public int BreakerWindow { get; init; } = 10;

    /// <summary>How old a failure may be and still count.</summary>
    public TimeSpan BreakerSampling { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>How long an open breaker refuses calls before it lets one probe through.</summary>
    public TimeSpan BreakerOpen { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The longest one HTTP call can spend in this layer: every try running to its timeout, every
    /// wait between them at the jitter ceiling, and every wait honouring the longest Retry-After.
    /// </summary>
    public TimeSpan MaxHttpDuration =>
        AttemptTimeout * (Retries + 1) + (MaxDelay + MaxRetryAfter) * Retries;

    /// <summary>The same for an email send, which has no Retry-After to honour.</summary>
    public TimeSpan MaxEmailDuration =>
        EmailAttemptTimeout * (Retries + 1) + MaxDelay * Retries;

    public TimeSpan MaxInnerDuration => MaxHttpDuration > MaxEmailDuration ? MaxHttpDuration : MaxEmailDuration;

    /// <summary>
    /// The shorter of the workflow runner's lease and the job queue's. A handler that runs past its
    /// lease is run again by another node, and the first node's outcome is discarded, so one action
    /// is performed twice.
    /// </summary>
    public static TimeSpan ShortestLease(int jobLeaseSeconds)
    {
        var jobs = TimeSpan.FromSeconds(jobLeaseSeconds);
        return jobs < WorkflowRetryPolicy.LeaseDuration ? jobs : WorkflowRetryPolicy.LeaseDuration;
    }

    public static OutboundResilienceOptions FromConfiguration(IConfiguration configuration)
    {
        var defaults = new OutboundResilienceOptions();
        return new OutboundResilienceOptions
        {
            Retries = configuration.GetValue(RetriesKey, defaults.Retries),
            AttemptTimeout = Seconds(configuration, AttemptTimeoutSecondsKey, defaults.AttemptTimeout),
            EmailAttemptTimeout = Seconds(configuration, EmailAttemptTimeoutSecondsKey, defaults.EmailAttemptTimeout),
            BaseDelay = TimeSpan.FromMilliseconds(configuration.GetValue(BaseDelayMillisecondsKey, defaults.BaseDelay.TotalMilliseconds)),
            MaxDelay = Seconds(configuration, MaxDelaySecondsKey, defaults.MaxDelay),
            MaxRetryAfter = Seconds(configuration, MaxRetryAfterSecondsKey, defaults.MaxRetryAfter),
            BreakerFailures = configuration.GetValue(BreakerFailuresKey, defaults.BreakerFailures),
            BreakerWindow = configuration.GetValue(BreakerWindowKey, defaults.BreakerWindow),
            BreakerSampling = Seconds(configuration, BreakerSamplingSecondsKey, defaults.BreakerSampling),
            BreakerOpen = Seconds(configuration, BreakerOpenSecondsKey, defaults.BreakerOpen),
        };
    }

    /// <summary>
    /// Refuses a setting out of range, and a budget that is not well under the shortest lease: at most
    /// half of it, so the call, the delivery row and the runner's own writes all finish in time.
    /// </summary>
    /// <param name="jobLeaseSeconds">
    /// <c>Jobs:LeaseSeconds</c>. Below one it is left to <c>JobOptions.Validate</c>, which names it.
    /// </param>
    public void Validate(int jobLeaseSeconds)
    {
        if (Retries is < 0 or > MaxRetries)
            throw new InvalidOperationException($"{RetriesKey} must be between 0 and {MaxRetries}.");
        if (AttemptTimeout <= TimeSpan.Zero)
            throw new InvalidOperationException($"{AttemptTimeoutSecondsKey} must be positive.");
        if (EmailAttemptTimeout <= TimeSpan.Zero)
            throw new InvalidOperationException($"{EmailAttemptTimeoutSecondsKey} must be positive.");
        if (MaxDelay <= TimeSpan.Zero)
            throw new InvalidOperationException($"{MaxDelaySecondsKey} must be positive.");
        if (BaseDelay < TimeSpan.Zero || BaseDelay > MaxDelay)
            throw new InvalidOperationException($"{BaseDelayMillisecondsKey} must be between 0 and {MaxDelaySecondsKey}.");
        if (MaxRetryAfter < TimeSpan.Zero)
            throw new InvalidOperationException($"{MaxRetryAfterSecondsKey} cannot be negative; 0 never waits for a Retry-After.");
        if (BreakerFailures < 0)
            throw new InvalidOperationException($"{BreakerFailuresKey} cannot be negative; 0 turns the breaker off.");
        if (BreakerFailures > 0 && BreakerWindow < BreakerFailures)
            throw new InvalidOperationException($"{BreakerWindowKey} must be at least {BreakerFailuresKey}.");
        if (BreakerFailures > 0 && (BreakerSampling <= TimeSpan.Zero || BreakerOpen <= TimeSpan.Zero))
            throw new InvalidOperationException($"{BreakerSamplingSecondsKey} and {BreakerOpenSecondsKey} must be positive.");

        if (jobLeaseSeconds < 1) return;

        var lease = ShortestLease(jobLeaseSeconds);
        if (MaxInnerDuration * 2 > lease)
        {
            throw new InvalidOperationException(
                $"The {Section} settings allow one outbound call to take up to {MaxInnerDuration.TotalSeconds:0.#} s, "
                + $"which is more than half of the shortest lease ({lease.TotalSeconds:0.#} s, the lower of the workflow "
                + "runner's 300 s and Jobs:LeaseSeconds). A call past its lease is run again by another node. "
                + "Lower the retries or the timeouts, or raise Jobs:LeaseSeconds.");
        }
    }

    private static TimeSpan Seconds(IConfiguration configuration, string key, TimeSpan fallback) =>
        TimeSpan.FromSeconds(configuration.GetValue(key, fallback.TotalSeconds));

    /// <summary>
    /// Part of every breaker's key. Carom keeps one breaker per key for the whole process and refuses
    /// a second registration of a key with different settings, so two hosts in one process with
    /// different settings (tests do this) must not share keys.
    /// </summary>
    internal string BreakerFingerprint =>
        $"{BreakerFailures}/{BreakerWindow}/{BreakerSampling.Ticks}/{BreakerOpen.Ticks}";
}
