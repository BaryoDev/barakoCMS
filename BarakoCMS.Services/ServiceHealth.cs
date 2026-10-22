namespace BarakoCMS.Services;

internal enum ServiceHealthState
{
    /// <summary>Reported within its interval.</summary>
    Healthy,

    /// <summary>Missed its interval, but not by enough to call it gone.</summary>
    Late,

    /// <summary>Silent for <see cref="ServiceHealth.DownAfterIntervals"/> intervals or more.</summary>
    Down,
}

/// <summary>
/// Turns a service's last heartbeat into a state. Pure, so the boundaries are testable without a clock.
/// </summary>
internal static class ServiceHealth
{
    public const int DefaultIntervalSeconds = 60;
    public const int MinIntervalSeconds = 5;
    public const int MaxIntervalSeconds = 86_400;

    /// <summary>Intervals of silence after which a service is Down rather than Late.</summary>
    public const int DownAfterIntervals = 3;

    /// <summary>
    /// A service that has never reported is measured from when it was registered, so a new one is
    /// Healthy for its first interval and then Late and Down like any other, instead of being Down
    /// the instant it exists.
    /// </summary>
    public static ServiceHealthState Evaluate(ServiceRegistration service, DateTime now)
    {
        var since = now - (service.LastHeartbeatAt ?? service.CreatedAt);
        var interval = TimeSpan.FromSeconds(Math.Max(1, service.HeartbeatIntervalSeconds));

        if (since <= interval) return ServiceHealthState.Healthy;
        return since < interval * DownAfterIntervals ? ServiceHealthState.Late : ServiceHealthState.Down;
    }
}
