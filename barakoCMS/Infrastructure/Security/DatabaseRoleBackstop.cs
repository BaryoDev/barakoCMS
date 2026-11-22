using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace barakoCMS.Infrastructure.Security;

/// <summary>
/// Runs <see cref="DatabaseRoleGuard"/> as the host starts, for a host that never called
/// <c>ApplyMartenSchemaAsync</c>, which is where the check normally runs.
/// </summary>
/// <remarks>
/// The check runs once per host. When the schema apply already ran it, this skips. A refusal here
/// stops the host from starting. The database commands (db-apply, db-assert and the rest) never
/// start the host, so they never reach it. Development skips it without touching the database.
/// </remarks>
internal sealed class DatabaseRoleBackstop(IServiceProvider services) : IHostedLifecycleService
{
    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        if (DevelopmentHost.Of(services.GetService<IHostEnvironment>()))
        {
            return;
        }

        await DatabaseRoleGuard.AssertAsync(
            services.GetRequiredService<IConfiguration>(),
            isDevelopment: false,
            services.GetRequiredService<IDocumentStore>(),
            services.GetRequiredService<ILoggerFactory>().CreateLogger("Database"),
            services.GetRequiredService<DatabaseRoleGuardKey>(),
            cancellationToken);
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// One per host, the key that says which start the role check has run for. The provider a hosted
/// service is given and the one <c>app.Services</c> returns are not the same object, so neither can
/// be the key.
/// </summary>
internal sealed class DatabaseRoleGuardKey;
