using barakoCMS.Modules;
using Marten;

namespace BarakoCMS.Pwa;

/// <summary>
/// Records PWA installs / installed-app launches. Register it with:
/// <code>services.AddBarakoCMS(config, m =&gt; m.Add(new PwaModule()));</code>
/// It exposes <c>POST /api/pwa/report</c> (called by the client, works anonymously or tied to the
/// signed-in user) and <c>GET /api/pwa/installs</c> (admin). Persists <see cref="PwaInstall"/> globally
/// — no per-tenant partition, the tenant is kept as data.
/// </summary>
public sealed class PwaModule : IBarakoModule
{
    public string Name => "Pwa";

    public void ConfigureMarten(StoreOptions options)
    {
        options.Schema.For<PwaInstall>()
            .SingleTenanted()
            .DocumentAlias("pwa_installs")
            .Index(x => x.DeviceId)
            .Index(x => x.UserId);
    }
}
