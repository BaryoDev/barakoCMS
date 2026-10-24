using barakoCMS.Modules;

namespace BarakoCMS.Services;

/// <summary>
/// A registry of the outside services barakoCMS talks to. Enable it with:
/// <code>services.AddBarakoCMS(config, m =&gt; m.Add(new ServicesModule()));</code>
///
/// <c>POST /api/services</c> registers a service, <c>POST /api/services/{name}/heartbeat</c> is how
/// it says it is alive, and <c>GET /api/services</c> lists each one as Healthy, Late or Down.
/// The registry is deployment-wide (SingleTenanted).
/// </summary>
public sealed class ServicesModule : IBarakoModule
{
    public string Name => "Services";

    public int HttpContractVersion => 1;

    public void ConfigureSchema(IModuleSchema schema)
    {
        schema.For<ServiceRegistration>()
            .SingleTenanted()
            .DocumentAlias("service_registrations")
            .Index(x => x.Name, i => i.IsUnique = true);
    }

    public Task SeedAsync(IModuleSeedContext context, CancellationToken ct) =>
        ServicesCapabilities.Defaults.GrantAsync(context, ct);

    [Obsolete("Override SeedAsync(IModuleSeedContext, CancellationToken), which names no Marten type. "
              + "Removal planned for barakoCMS 6.0.0.")]
    public Task SeedAsync(Marten.IDocumentSession session, IServiceProvider services, CancellationToken ct)
    {
#pragma warning disable CS0618
        return SeedAsync(ModuleSeedContexts.Over(session, services), ct);
#pragma warning restore CS0618
    }
}
