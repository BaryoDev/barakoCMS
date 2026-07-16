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

    private static string ClientIp(HttpContext ctx)
    {
        // Behind nginx/Caddy the real client is the first X-Forwarded-For hop.
        var forwarded = ctx.Request.Headers["X-Forwarded-For"].ToString();
        if (!string.IsNullOrWhiteSpace(forwarded))
            return forwarded.Split(',')[0].Trim();
        return ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }

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
