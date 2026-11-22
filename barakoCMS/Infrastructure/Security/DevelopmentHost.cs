using Microsoft.Extensions.Hosting;

namespace barakoCMS.Infrastructure.Security;

/// <summary>
/// The one place every startup guard and every environment default decides whether the host is
/// Development. It reads the <see cref="IHostEnvironment"/> the host registered, and nothing else.
/// </summary>
/// <remarks>
/// Not the process variable and not a configuration key named <c>environment</c>: a process can hold
/// hosts in more than one environment, as a test process does, and a value any configuration
/// source can set is not a decision a guard should hang on. A host that registered none is treated
/// as Production, so the guards apply.
/// </remarks>
internal static class DevelopmentHost
{
    /// <summary>Whether the host being built is Development, from the environment it registered in <paramref name="services"/>.</summary>
    /// <remarks>
    /// Both <c>WebApplicationBuilder</c> and the generic host register the instance, and the later
    /// registration wins, as it does for resolution.
    /// </remarks>
    public static bool Of(IServiceCollection services)
    {
        for (var i = services.Count - 1; i >= 0; i--)
        {
            var descriptor = services[i];
            if (typeof(IHostEnvironment).IsAssignableFrom(descriptor.ServiceType)
                && !descriptor.IsKeyedService
                && descriptor.ImplementationInstance is IHostEnvironment environment)
            {
                return Of(environment);
            }
        }

        return false;
    }

    /// <summary>Whether <paramref name="environment"/> is Development. Null is Production.</summary>
    public static bool Of(IHostEnvironment? environment) =>
        environment is not null
        && barakoCMS.Extensions.ServiceCollectionExtensions.IsDevelopment(environment.EnvironmentName);

    /// <summary>The environment name the middleware that still takes a name compares against: Development or Production.</summary>
    public static string NameFor(IServiceProvider services) =>
        Of(services.GetService<IHostEnvironment>()) ? Environments.Development : Environments.Production;
}
