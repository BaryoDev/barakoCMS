using barakoCMS.Modules;

namespace BarakoCMS.Reports;

/// <summary>
/// Saved reports over a client's own content: group, count and sum, computed in the database and
/// read through the caller's own permissions.
/// </summary>
public sealed class ReportsModule : IBarakoModule
{
    public string Name => "Reports";

    public int HttpContractVersion => 1;

    public void ConfigureSchema(IModuleSchema schema)
    {
        // Per tenant, like the content types a report points at.
        schema.For<ReportDefinition>()
            .DocumentAlias("report_definitions")
            .Identity(x => x.Name);
    }

    /// <summary>
    /// Gives this module's capability to Admin, which core cannot do because it does not know the
    /// module exists. Additive and idempotent.
    /// </summary>
    public Task SeedAsync(IModuleSeedContext context, CancellationToken ct) =>
        ReportsCapabilities.Defaults.GrantAsync(context, ct);

    [Obsolete("Override SeedAsync(IModuleSeedContext, CancellationToken), which names no Marten type. "
              + "Removal planned for barakoCMS 6.0.0.")]
    public Task SeedAsync(Marten.IDocumentSession session, IServiceProvider services, CancellationToken ct)
    {
#pragma warning disable CS0618
        return SeedAsync(ModuleSeedContexts.Over(session, services), ct);
#pragma warning restore CS0618
    }
}
