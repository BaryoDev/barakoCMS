using barakoCMS.Modules;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BarakoCMS.Push;

public sealed class PushModule : IBarakoModule
{
    public string Name => "Push";

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddSingleton(PushOptions.From(configuration));
}
