using Microsoft.AspNetCore.Http;

namespace barakoCMS.Infrastructure;

/// <summary>
/// The device/client behind an HTTP request: raw user-agent, client IP, an optional client-supplied
/// device id (<c>X-Device-Id</c>), and a friendly one-line description. Used by the OTP email and by
/// the DeviceTrust module. Reading it is generic; trusting/binding a device is the module's job.
/// </summary>
public sealed record DeviceContext(string UserAgent, string IpAddress, string? DeviceId, string Description)
{
    public const string DeviceIdHeader = "X-Device-Id";

    public static DeviceContext From(HttpContext ctx)
    {
        var ua = ctx.Request.Headers.UserAgent.ToString();
        var deviceId = ctx.Request.Headers[DeviceIdHeader].ToString();
        return new DeviceContext(
            UserAgent: ua,
            IpAddress: ClientIp(ctx),
            DeviceId: string.IsNullOrWhiteSpace(deviceId) ? null : deviceId.Trim(),
            Description: Describe(ua));
    }

    // X-Forwarded-For is not read here. It is client-supplied, so reading it directly let any
    // caller write its own address into the audit log and the OTP email. Behind a proxy the header
    // is applied by the ForwardedHeaders middleware, which only honours it from a hop the operator
    // named in ForwardedHeaders:KnownProxies; by the time this runs, RemoteIpAddress is already the
    // client. With the feature off this is the proxy's address, which is wrong but not forgeable.
    private static string ClientIp(HttpContext ctx) =>
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    /// <summary>Best-effort "Browser on OS" summary from a user-agent, falling back to the raw string.</summary>
    public static string Describe(string ua)
    {
        if (string.IsNullOrWhiteSpace(ua))
            return "an unknown device";

        var browser =
            ua.Contains("Edg") ? "Edge" :
            ua.Contains("OPR") || ua.Contains("Opera") ? "Opera" :
            ua.Contains("Chrome") ? "Chrome" :
            ua.Contains("Firefox") ? "Firefox" :
            ua.Contains("Safari") ? "Safari" :
            null;

        var os =
            ua.Contains("iPhone") || ua.Contains("iPad") ? "iOS" :
            ua.Contains("Android") ? "Android" :
            ua.Contains("Mac OS X") || ua.Contains("Macintosh") ? "macOS" :
            ua.Contains("Windows") ? "Windows" :
            ua.Contains("Linux") ? "Linux" :
            null;

        if (browser != null && os != null) return $"{browser} on {os}";
        if (browser != null) return browser;
        if (os != null) return os;
        return ua.Length > 80 ? ua[..80] + "…" : ua;
    }
}
