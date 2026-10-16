using barakoCMS.Features.Workflows;
using barakoCMS.Modules;
using Marten;
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
            .RemoveAllLoggers() // the default handlers log the full request URL, which is the endpoint
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        services.AddScoped<IPushTransport>(sp => options.IsConfigured
            ? sp.GetRequiredService<WebPushTransport>()
            : new NullPushTransport());
        services.AddScoped<IPushSender, PushSender>();
        services.AddScoped<IWorkflowAction, PushAction>();
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

    public Task SeedAsync(IDocumentSession session, IServiceProvider services, CancellationToken ct) =>
        PushCapabilities.Defaults.GrantAsync(session, ct);
}
