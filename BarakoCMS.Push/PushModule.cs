using barakoCMS.Modules;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BarakoCMS.Push;

public sealed class PushModule : IBarakoModule
{
    public string Name => "Push";

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        var options = PushOptions.From(configuration);
        services.AddSingleton(options);
        services.AddHttpClient<WebPushTransport>()
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        services.AddScoped<IPushTransport>(sp => options.IsConfigured
            ? sp.GetRequiredService<WebPushTransport>()
            : new NullPushTransport());
        services.AddScoped<IPushSender, PushSender>();
    }

    private sealed class NullPushTransport : IPushTransport
    {
        public Task<int> SendAsync(StoredSubscription subscription, string payloadJson, CancellationToken ct) =>
            throw new InvalidOperationException("Push is not configured.");
    }

    public void ConfigureSchema(IModuleSchema schema)
    {
        schema.For<StoredSubscription>()
            .DocumentAlias("push_subscriptions")
            .Index(x => x.Endpoint, idx =>
            {
                idx.IsUnique = true;
                idx.TenancyScope = Marten.Schema.Indexing.Unique.TenancyScope.PerTenant;
            })
            .Index(x => x.UserId);
    }
}
