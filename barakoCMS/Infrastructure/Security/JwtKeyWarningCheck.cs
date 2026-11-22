using barakoCMS.Infrastructure.Auth;

namespace barakoCMS.Infrastructure.Security;

/// <summary>
/// Logs the non-ASCII <c>JWT:Key</c> warning once per host, through that host's logger, so a site
/// that hosts core as a package sees it without a process-wide Serilog logger.
/// </summary>
internal sealed class JwtKeyWarningCheck(
    IConfiguration configuration, ILogger<JwtKeyWarningCheck> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (JwtKeySource.Read(configuration) is { } key && JwtKeyGuard.NonAsciiWarning(key) is { } warning)
            logger.LogWarning("{Warning}", warning);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
