using barakoCMS.Modules;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BarakoCMS.Scheduling;

/// <summary>
/// Scheduling for other modules. Enable it with:
/// <code>services.AddBarakoCMS(config, m =&gt; m.Add(new SchedulingModule()));</code>
///
/// Inject <see cref="IScheduler"/> to schedule or cancel an entry, and register an
/// <see cref="IScheduledActionHandler"/> per action name to receive it. The module has no endpoints.
/// </summary>
public sealed class SchedulingModule : IBarakoModule
{
    public string Name => "Scheduling";

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IScheduler, Scheduler>();
        services.AddSingleton<ScheduleSweepService>();
        services.AddHostedService(sp => sp.GetRequiredService<ScheduleSweepService>());
    }

    public void ConfigureSchema(IModuleSchema schema)
    {
        schema.For<ScheduledEntry>()
            .SingleTenanted()
            .DocumentAlias("scheduled_entries")
            .UseNumericRevisions(true)
            .Index(x => x.DueAt)
            .Index(x => x.Status);
    }
}
