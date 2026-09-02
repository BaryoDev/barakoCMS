using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace barakoCMS.Infrastructure.Health;

internal enum StartupSeedState
{
    Completed,
    Pending,
    Failed
}

/// <summary>
/// Holds readiness closed while a host seeds the baseline roles and the initial admin.
/// </summary>
/// <remarks>
/// The core host seeds on a background task so the process can answer probes while the seed runs.
/// Without a gate the pod is ready the moment Kestrel binds, and a request landing in that window
/// sees no roles: the configured admin cannot sign in because the user does not exist yet, and a
/// registration is stored with an empty RoleIds because Register skips a role it cannot find.
///
/// It starts <see cref="StartupSeedState.Completed"/> deliberately. A host that seeds inline before
/// app.Run(), which BarakoCMS.Suite does, never calls <see cref="MarkPending"/>, so this can only
/// ever hold readiness for a host that asked it to. See issue #256.
/// </remarks>
internal sealed class StartupSeedGate
{
    private volatile StartupSeedState _state = StartupSeedState.Completed;
    private volatile string _detail = "No startup seed was declared by this host.";

    public StartupSeedState State => _state;

    public string Detail => _detail;

    public void MarkPending()
    {
        _detail = "Seeding roles and the initial admin.";
        _state = StartupSeedState.Pending;
    }

    public void MarkCompleted()
    {
        _detail = "Startup seeding finished.";
        _state = StartupSeedState.Completed;
    }

    public void MarkFailed(Exception exception)
    {
        _detail = $"Startup seeding failed: {exception.GetType().Name}.";
        _state = StartupSeedState.Failed;
    }
}

/// <summary>
/// Readiness view of <see cref="StartupSeedGate"/>. Tagged "ready" only, never "live": a node whose
/// seed has not finished must stay out of rotation, but restarting it only starts the seed over.
/// </summary>
internal sealed class StartupSeedHealthCheck : IHealthCheck
{
    private readonly StartupSeedGate _gate;

    public StartupSeedHealthCheck(StartupSeedGate gate) => _gate = gate;

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(_gate.State == StartupSeedState.Completed
            ? HealthCheckResult.Healthy(_gate.Detail)
            : HealthCheckResult.Unhealthy(_gate.Detail));
}
