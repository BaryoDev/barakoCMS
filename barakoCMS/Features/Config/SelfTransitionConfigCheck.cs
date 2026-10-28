namespace barakoCMS.Features.Config;

/// <summary>
/// Warns at start when <c>Lifecycle:AllowSelfTransition:{Transition}</c> is set without a content
/// type. That form allows the transition on every type that declares it, which is rarely what an
/// operator meant. It is still honoured so an existing install does not change behaviour.
/// </summary>
internal sealed class SelfTransitionConfigCheck(
    IConfiguration configuration, ILogger<SelfTransitionConfigCheck> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var key in UnscopedKeys(configuration))
        {
            logger.LogWarning(
                "Lifecycle:AllowSelfTransition:{Transition} has no content type, so it applies to every content type with a transition of that name. Use Lifecycle:AllowSelfTransition:<ContentType>:{Transition}.",
                key, key);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>The children that hold a value of their own. A scoped entry is a section with children and no value.</summary>
    internal static IReadOnlyList<string> UnscopedKeys(IConfiguration configuration) =>
        configuration.GetSection("Lifecycle:AllowSelfTransition").GetChildren()
            .Where(c => c.Value is not null)
            .Select(c => c.Key)
            .ToList();
}
