using k8s;

namespace barakoCMS.Infrastructure.Services;

/// <summary>
/// Hands out a Kubernetes client, rebuilding it after a failure instead of giving up for good.
/// </summary>
/// <remarks>
/// The monitor used to build its client once in the constructor and latch a static bool on failure.
/// The service is a singleton, so one API-server hiccup at pod start disabled Kubernetes monitoring
/// for the whole process lifetime, with a restart as the only recovery. Blips at pod start are
/// normal in the environment this feature targets. See issue #264.
///
/// Two failure kinds, deliberately treated differently:
///
///   the factory returns null   no in-cluster service account and no kubeconfig. That is a
///                              legitimate steady state, not a fault, so it is retried on a slow
///                              fixed interval and logged once rather than every call.
///   the factory throws         it tried and failed. Retried on exponential backoff from
///                              <see cref="MinBackoff"/> up to <see cref="MaxBackoff"/>.
/// </remarks>
internal sealed class KubernetesClientProvider
{
    internal static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan UnconfiguredInterval = TimeSpan.FromMinutes(5);

    private readonly Func<Kubernetes?> _factory;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ILogger _logger;
    private readonly object _gate = new();

    private Kubernetes? _client;
    private DateTimeOffset _nextAttempt = DateTimeOffset.MinValue;
    private TimeSpan _backoff = MinBackoff;
    private bool _loggedUnconfigured;

    public KubernetesClientProvider(Func<Kubernetes?> factory, ILogger logger, Func<DateTimeOffset>? clock = null)
    {
        _factory = factory;
        _logger = logger;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>How many times the factory has been called. For tests.</summary>
    internal int Attempts { get; private set; }

    public Kubernetes? GetClient()
    {
        lock (_gate)
        {
            if (_client is not null)
                return _client;

            var now = _clock();
            if (now < _nextAttempt)
                return null;

            Attempts++;

            try
            {
                var client = _factory();

                if (client is null)
                {
                    _nextAttempt = now + UnconfiguredInterval;
                    if (!_loggedUnconfigured)
                    {
                        _loggedUnconfigured = true;
                        _logger.LogInformation(
                            "No Kubernetes configuration is available. This is normal outside a cluster; monitoring stays off until one appears.");
                    }
                    return null;
                }

                _client = client;
                _backoff = MinBackoff;
                _loggedUnconfigured = false;
                return _client;
            }
            catch (Exception ex)
            {
                _nextAttempt = now + _backoff;
                _logger.LogWarning(
                    ex,
                    "Failed to initialize the Kubernetes client. Retrying in {Backoff}.",
                    _backoff);
                _backoff = _backoff >= MaxBackoff
                    ? MaxBackoff
                    : TimeSpan.FromTicks(Math.Min(_backoff.Ticks * 2, MaxBackoff.Ticks));
                return null;
            }
        }
    }
}
