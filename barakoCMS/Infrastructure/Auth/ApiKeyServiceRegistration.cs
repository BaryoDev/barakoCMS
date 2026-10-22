using Microsoft.Extensions.DependencyInjection.Extensions;

namespace barakoCMS.Infrastructure.Auth;

internal static class ApiKeyServiceRegistration
{
    public static IServiceCollection AddApiKeyServices(this IServiceCollection services)
    {
        services.AddSingleton<ApiKeyService>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ApiKeyRefusals>();
        return services;
    }
}
