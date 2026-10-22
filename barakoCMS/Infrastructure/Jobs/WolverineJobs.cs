using barakoCMS.Core.Interfaces;
using Marten;
using Wolverine;
using Wolverine.Marten;

namespace barakoCMS.Infrastructure.Jobs;

/// <summary>
/// Switches durable work onto Wolverine. Off unless <see cref="EnabledKey"/> is true, so a
/// deployment that does not opt in keeps the Marten job storage and gains no tables.
/// </summary>
public static class WolverineJobs
{
    public const string EnabledKey = "Jobs:Wolverine:Enabled";

    /// <summary>The local queue every durable message travels on.</summary>
    public const string QueueName = "barako-durable";

    public static bool IsEnabled(IConfiguration configuration) =>
        configuration.GetValue(EnabledKey, false);

    /// <summary>
    /// Puts Wolverine's envelope storage in the Marten store and registers
    /// <see cref="IDurableOutbox"/>. Solo mode, since this slice runs one node.
    /// </summary>
    public static MartenServiceCollectionExtensions.MartenConfigurationExpression IntegrateDurableWork(
        this MartenServiceCollectionExtensions.MartenConfigurationExpression marten,
        Action<MartenIntegration>? configure = null)
    {
        marten.Services.AddWolverine(options =>
        {
            options.Durability.Mode = DurabilityMode.Solo;
            options.Discovery.DisableConventionalDiscovery().IncludeType<DurableMessageDispatcher>();
            options.PublishMessage<DurableMessage>().ToLocalQueue(QueueName).UseDurableInbox();
        });
        marten.Services.AddSingleton<DurableTenantSessionFactory>();
        marten.Services.AddSingleton<DurableTenantPublisher>();
        marten.Services.AddScoped<IDurableOutbox, WolverineDurableOutbox>();
        return marten.IntegrateWithWolverine(configure ?? (_ => { }));
    }
}
