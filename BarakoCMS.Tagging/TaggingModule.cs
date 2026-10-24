using barakoCMS.Modules;

namespace BarakoCMS.Tagging;

/// <summary>
/// Vocabularies, terms that can nest, and term assignments on any record. A record is a content
/// type name and an id, and nothing here reads or writes it, so content entries, files and other
/// modules' records are tagged through the same endpoints.
/// </summary>
public sealed class TaggingModule : IBarakoModule
{
    public string Name => "Tagging";

    public int HttpContractVersion => 1;

    public void ConfigureServices(Microsoft.Extensions.DependencyInjection.IServiceCollection services, Microsoft.Extensions.Configuration.IConfiguration configuration) =>
        services.AddScoped<barakoCMS.Core.Interfaces.IContentErasureCascade, TermsErasureCascade>();

    public void ConfigureSchema(IModuleSchema schema)
    {
        // All three are per tenant, so a vocabulary, a term or an assignment in one tenant is not
        // found from another.
        schema.For<Vocabulary>().DocumentAlias("tagging_vocabularies");
        schema.For<Term>().DocumentAlias("tagging_terms").Index(x => x.VocabularyId);
        schema.For<TermAssignment>().DocumentAlias("tagging_term_assignments")
            .Index(x => x.TermId)
            .Index(x => x.RecordId);
    }

    /// <summary>Gives this module's capabilities to Admin. Additive and idempotent.</summary>
    public Task SeedAsync(IModuleSeedContext context, CancellationToken ct) =>
        TaggingCapabilities.Defaults.GrantAsync(context, ct);

    [Obsolete("Override SeedAsync(IModuleSeedContext, CancellationToken), which names no Marten type. "
              + "Removal planned for barakoCMS 6.0.0.")]
    public Task SeedAsync(Marten.IDocumentSession session, IServiceProvider services, CancellationToken ct)
    {
#pragma warning disable CS0618
        return SeedAsync(ModuleSeedContexts.Over(session, services), ct);
#pragma warning restore CS0618
    }
}
