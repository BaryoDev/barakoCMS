using barakoCMS.Modules;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BarakoCMS.Qr;

/// <summary>
/// Renders a field of a public entry as a QR code. Enable it with:
/// <code>services.AddBarakoCMS(config, m =&gt; m.Add(new QrModule()));</code>
/// and list the encodable fields under <c>Modules:Qr:Fields</c>. It stores nothing.
/// </summary>
public sealed class QrModule : IBarakoModule
{
    public string Name => "Qr";

    public int ContractVersion => ModuleContract.Version;

    public int HttpContractVersion => QrContract.Version;

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<QrOptions>()
            .Bind(configuration)
            .Validate(o => o.MaxValueLength is >= 1 and <= 2048,
                "Modules:Qr:MaxValueLength must be between 1 and 2048.")
            .Validate(o => o.MaxPixelsPerModule is >= 1 and <= 50,
                "Modules:Qr:MaxPixelsPerModule must be between 1 and 50.")
            .ValidateOnStart();
    }
}
