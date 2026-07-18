using barakoCMS.Modules;
using Marten;

namespace BarakoCMS.Diagnostics;

/// <summary>
/// Client error logging for barakoCMS. Enable it with:
/// <code>services.AddBarakoCMS(config, m =&gt; m.Add(new DiagnosticsModule()));</code>
///
/// Apps POST captured browser errors to <c>POST /api/client-errors</c> (anonymous — errors happen
/// before sign-in too). They're deduplicated by fingerprint and browsable by officers at
/// <c>GET /api/client-errors</c>; mark one done with <c>POST /api/client-errors/{id}/resolve</c>.
/// Errors are stored globally (SingleTenanted) with the originating club kept as data.
/// </summary>
public sealed class DiagnosticsModule : IBarakoModule
{
    public string Name => "Diagnostics";

    public void ConfigureMarten(StoreOptions options)
    {
        options.Schema.For<ClientError>()
            .SingleTenanted()
            .DocumentAlias("client_errors")
            .Index(x => x.Fingerprint)
            .Index(x => x.LastSeenAt)
            .Index(x => x.Resolved);
    }
}
