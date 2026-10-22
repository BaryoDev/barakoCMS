using barakoCMS.Features.Workflows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>Runs the workflow runner until it has nothing due, the way WorkflowRunTests does.</summary>
internal static class RunnerDrain
{
    public static async Task DrainAsync(IServiceProvider services, CancellationToken ct)
    {
        var runner = new WorkflowRunner(
            services,
            services.GetRequiredService<ILogger<WorkflowRunner>>(),
            services.GetRequiredService<IConfiguration>());

        var polls = 0;
        while (await runner.RunOnceAsync(ct))
        {
            if (++polls > 200) throw new InvalidOperationException("The runner never went idle.");
        }
    }
}
