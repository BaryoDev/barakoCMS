using barakoCMS.Features.Workflows;
using barakoCMS.Modules;
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
        schema.For<DocumentTemplate>().DocumentAlias("document_templates").Index(x => x.Slug);
        schema.For<DocumentRender>().DocumentAlias("document_renders");
    }
}
