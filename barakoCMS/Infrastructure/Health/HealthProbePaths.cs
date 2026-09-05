namespace barakoCMS.Infrastructure.Health;

/// <summary>
/// The health probe paths a kubelet (or anything else polling liveness/readiness) reads. None of
/// them may ever be served from the output cache: a probe stuck on a stale cached failure while the
/// app is still starting can never open, and a stale cached failure served after the app recovers
/// keeps a healthy pod out of rotation. See #545, where output caching became global middleware for
/// the first time.
/// </summary>
public static class HealthProbePaths
{
    public static bool IsHealthPath(string? path) =>
        path is not null
        && (string.Equals(path, "/health", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/health/", StringComparison.OrdinalIgnoreCase));
}
