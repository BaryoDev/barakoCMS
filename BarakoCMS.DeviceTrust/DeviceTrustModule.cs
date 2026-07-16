using barakoCMS.Modules;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BarakoCMS.DeviceTrust;

/// <summary>
/// Optional trusted-device module for barakoCMS. Enable it with:
/// <code>services.AddBarakoCMS(config, m =&gt; m.Add(new DeviceTrustModule()));</code>
///
/// Records the device behind each sign-in and shows it in the OTP email. OTP sign-in trusts the
/// device; with <c>DeviceTrust:Enforce=true</c>, password sign-in from an unknown device asks for an
/// OTP to approve it, tokens are bound to their device (<c>did</c> claim), and authenticated API
/// requests from an unapproved device are rejected. Adds <c>GET /api/devices</c> and
/// <c>POST /api/devices/{id}/revoke</c>.
/// </summary>
public sealed class DeviceTrustModule : IBarakoModule
{
    public string Name => "DeviceTrust";

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<IDeviceTrustService, DeviceTrustService>();
        // Override the core no-op gate.
        services.AddScoped<barakoCMS.Core.Interfaces.IDeviceGate, DeviceGate>();
        // Contribute the enforcement pre-processor (core applies all DI-registered global processors).
        services.AddSingleton<FastEndpoints.IGlobalPreProcessor, DeviceEnforcementProcessor>();
    }

    public void ConfigureMarten(StoreOptions options)
    {
        // Devices belong to the global user identity, not a single club — a device trusted while
        // signing into one club must stay trusted when the user switches to another. Keep it global.
        options.Schema.For<Device>()
            .SingleTenanted()
            .DocumentAlias("devices")
            .Index(x => x.UserId)
            .Index(x => x.DeviceId);
    }
}
