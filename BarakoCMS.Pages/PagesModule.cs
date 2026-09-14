using barakoCMS.Core.Interfaces;
using barakoCMS.Modules;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BarakoCMS.Pages;

/// <summary>
/// A page tree over an existing content type. Enable it with:
/// <code>services.AddBarakoCMS(config, m =&gt; m.Add(new PagesModule()));</code>
/// </summary>
/// <remarks>
/// It stores no documents of its own, so there is no schema. It seeds nothing: creating the page type
/// on boot would make a later <c>blog</c> blueprint apply fail with 409.
/// </remarks>
public sealed class PagesModule : IBarakoModule
{
    public string Name => "Pages";

    public int ContractVersion => ModuleContract.Version;

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<PagesOptions>()
            .Bind(configuration)
            .Validate(o => !string.IsNullOrWhiteSpace(o.ContentType)
                           && !string.IsNullOrWhiteSpace(o.ParentField)
                           && !string.IsNullOrWhiteSpace(o.ShowInNavigationField)
                           && !string.IsNullOrWhiteSpace(o.OrderField)
                           && !string.IsNullOrWhiteSpace(o.TitleField),
                "Modules:Pages field names may not be empty.")
            .Validate(o => o.MaxDepth >= 1, "Modules:Pages:MaxDepth must be at least 1.")
            .Validate(o => o.MaxPages >= 1, "Modules:Pages:MaxPages must be at least 1.")
            .ValidateOnStart();

        services.AddScoped<IContentLifecycleHook, PageTreeHook>();
    }
}
