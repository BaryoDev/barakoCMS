using barakoCMS.Core.Interfaces;
using Microsoft.Extensions.Hosting;

namespace BarakoCMS.Pages;

/// <summary>Builds <see cref="BlockDataSources"/> at start, so a bad registration stops the host rather than a request.</summary>
internal sealed class BlockDataSourcesCheck(BlockDataSources sources) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = sources.All;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
