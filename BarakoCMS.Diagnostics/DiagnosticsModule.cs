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

    public void ConfigureSchema(IModuleSchema schema)
    {
        schema.For<ClientError>()
            .SingleTenanted()
            .DocumentAlias("client_errors")
            .Index(x => x.Fingerprint)
            .Index(x => x.LastSeenAt)
            .Index(x => x.Resolved);
    }

    /// <summary>
    /// Gives this module's capabilities to the roles that already reached its endpoints.
    /// </summary>
    /// <remarks>
    /// Core cannot do this: <c>SystemCapabilities.DefaultsFor</c> does not know this module exists.
    /// Without it the endpoints would be reachable only through the legacy role-name fallback, and
    /// turning that off, which is the point of issue #443, would take the module away from every
    /// Admin. Additive and idempotent, and it skips a role the host never seeded.
    /// </remarks>
    public Task SeedAsync(IDocumentSession session, IServiceProvider services, CancellationToken ct) =>
        ModuleCapabilities.GrantAsync(session, DiagnosticsCapabilities.SeededRoles, DiagnosticsCapabilities.All, ct);
}
