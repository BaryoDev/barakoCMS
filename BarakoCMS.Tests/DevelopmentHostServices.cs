using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;

namespace BarakoCMS.Tests;

/// <summary>
/// Gives a bare <see cref="IServiceCollection"/> the Development host environment a real host
/// registers. The startup guards read that registration and treat a host with none as Production,
/// so a test that builds services by hand and calls <c>AddBarakoCMS</c> says here that it is a
/// Development host.
/// </summary>
internal static class DevelopmentHostServices
{
    public static IServiceCollection AsDevelopmentHost(this IServiceCollection services) =>
        services.AsHost(Environments.Development);

    public static IServiceCollection AsHost(this IServiceCollection services, string environmentName)
    {
        services.AddSingleton<IHostEnvironment>(new HostingEnvironment { EnvironmentName = environmentName });
        return services;
    }
}
