using barakoCMS.Extensions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// A host started with flags and no command serves. WebApplicationFactory passes settings such as
/// <c>--HTTP_PORTS=</c> to Main when the environment sets them, and the test server waits for Run.
/// </summary>
public class BarakoCommandExtensionsTests
{
    private sealed class StartsThenStops(IHostApplicationLifetime lifetime) : IHostedService
    {
        public static bool Started;

        public Task StartAsync(CancellationToken ct)
        {
            Started = true;
            lifetime.StopApplication();
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }

    [Theory]
    [InlineData("--HTTP_PORTS=")]
    [InlineData("--urls=http://127.0.0.1:0")]
    public async Task A_flag_with_no_command_runs_the_host(string flag)
    {
        StartsThenStops.Started = false;
        using var host = new HostBuilder()
            .ConfigureServices(s => s.AddHostedService<StartsThenStops>())
            .Build();

        var exit = await host.RunBarakoCommandsAsync([flag]);

        exit.Should().Be(0);
        StartsThenStops.Started.Should().BeTrue();
    }
}
