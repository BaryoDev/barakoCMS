using barakoCMS.Infrastructure;
using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BarakoCMS.DeviceTrust;

/// <summary>
/// Blocks authenticated requests that don't come from the token's bound, trusted device — only when
/// <c>DeviceTrust:Enforce=true</c>. Tokens without a <c>did</c> claim (anonymous endpoints, or issued
/// before device trust) pass through, so turning enforcement on can't lock out existing anonymous flows.
/// </summary>
public sealed class DeviceEnforcementProcessor : IGlobalPreProcessor
{
    public async Task PreProcessAsync(IPreProcessorContext context, CancellationToken ct)
    {
        var http = context.HttpContext;
        if (http.User.Identity is not { IsAuthenticated: true })
            return;

        var config = http.RequestServices.GetRequiredService<IConfiguration>();
        if (!config.GetValue<bool>("DeviceTrust:Enforce"))
            return;

        var boundDeviceId = http.User.FindFirst(DeviceGate.DeviceClaim)?.Value;
        if (string.IsNullOrEmpty(boundDeviceId))
            return; // token isn't device-bound — nothing to enforce

        var header = http.Request.Headers[DeviceContext.DeviceIdHeader].ToString();
        Guid.TryParse(http.User.FindFirst("UserId")?.Value, out var userId);

        var trusted = !string.IsNullOrEmpty(header)
            && header == boundDeviceId
            && userId != Guid.Empty
            && await http.RequestServices.GetRequiredService<IDeviceTrustService>()
                .IsTrustedAsync(userId, boundDeviceId, ct);

        if (!trusted)
        {
            http.Response.StatusCode = 401;
            await http.Response.WriteAsync("This device is not approved.", ct);
            // Short-circuit: a validation failure stops FastEndpoints from running the endpoint.
            context.ValidationFailures.Add(
                new FluentValidation.Results.ValidationFailure(DeviceContext.DeviceIdHeader, "Device not approved"));
        }
    }
}
