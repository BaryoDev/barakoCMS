using barakoCMS.Modules;

namespace BarakoCMS.Contacts;

/// <summary>
/// One record for a person or an organization, for any module or product that needs a party.
/// Enable it with:
/// <code>services.AddBarakoCMS(config, m =&gt; m.Add(new ContactsModule()));</code>
/// </summary>
/// <remarks>
/// Custom fields are the tenant's own: they are declared on the <c>contact-record</c> content type
/// with the ordinary content type endpoints and validated from there. The same type says which
/// fields are Sensitive, so email, phone and address never leave over anonymous delivery.
/// </remarks>
public sealed class ContactsModule : IBarakoModule
{
    public string Name => "Contacts";

    public int HttpContractVersion => 1;

    public void ConfigureSchema(IModuleSchema schema)
    {
        schema.For<Contact>()
            .DocumentAlias("contacts")
            .UseOptimisticConcurrency(true)
            .Index(x => x.EmailKey, idx =>
            {
                idx.IsUnique = true;
                idx.TenancyScope = Marten.Schema.Indexing.Unique.TenancyScope.PerTenant;
            })
            .Index(x => x.Kind);

        schema.For<ContactLink>()
            .DocumentAlias("contact_links")
            .Index(x => x.PersonId)
            .Index(x => x.OrganizationId);
    }

    public async Task SeedAsync(IModuleSeedContext context, CancellationToken ct)
    {
        await ContactsCapabilities.Defaults.GrantAsync(context, ct);
        await context.EnsureContentTypeAsync(ContactRecord.Definition(), ct);
    }

    [Obsolete("Override SeedAsync(IModuleSeedContext, CancellationToken), which names no Marten type. "
              + "Removal planned for barakoCMS 6.0.0.")]
    public Task SeedAsync(Marten.IDocumentSession session, IServiceProvider services, CancellationToken ct)
    {
#pragma warning disable CS0618
        return SeedAsync(ModuleSeedContexts.Over(session, services), ct);
#pragma warning restore CS0618
    }
}
