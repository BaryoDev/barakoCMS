using barakoCMS.Features.Workflows;
using barakoCMS.Modules;
using Marten.Schema.Indexing.Unique;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BarakoCMS.Documents;

/// <summary>
/// Document generation for barakoCMS. Enable it with:
/// <code>services.AddBarakoCMS(config, m =&gt; m.Add(new DocumentsModule()));</code>
/// Adds the <c>RenderDocument</c> workflow action. It stores the PDF through the Files module, so
/// enable that as well.
/// </summary>
public sealed class DocumentsModule : IBarakoModule
{
    public string Name => "Documents";

    public int ContractVersion => ModuleContract.Version;

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<IWorkflowAction, RenderDocumentAction>();
    }

    public void ConfigureSchema(IModuleSchema schema)
    {
        // One template per slug per tenant. The action takes the first match, so a second row with
        // the same slug would make the document depend on which one a read found first.
        // migrations/5.2.0/585-documents.sql adds it to an existing database, under this name.
        schema.For<DocumentTemplate>().DocumentAlias("document_templates").Index(x => x.Slug, idx =>
        {
            idx.IsUnique = true;
            idx.TenancyScope = TenancyScope.PerTenant;
        });
        schema.For<DocumentRender>().DocumentAlias("document_renders");
    }
}
