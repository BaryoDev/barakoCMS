using Microsoft.Extensions.Configuration;

namespace BarakoCMS.Push;

public sealed class PushOptions
{
    public string? PublicKey { get; init; }
    public string? PrivateKey { get; init; }
    public string? Subject { get; init; }

    public static readonly string[] DefaultAllowedHosts =
    [
        "fcm.googleapis.com",
        "*.push.services.mozilla.com",
        "*.notify.windows.com",
        "web.push.apple.com",
        "*.push.apple.com",
    ];

    public IReadOnlyList<string> AllowedHosts { get; init; } = DefaultAllowedHosts;

    public TimeSpan SendTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public bool IsConfigured => PublicKey is not null && PrivateKey is not null;

    public static PushOptions From(IConfiguration configuration)
    {
        var pub = configuration["PublicKey"];
        var priv = configuration["PrivateKey"];
        var subject = configuration["Subject"]?.Trim();
        var timeout = int.TryParse(configuration["SendTimeoutSeconds"], out var secs) && secs > 0
            ? TimeSpan.FromSeconds(secs)
            : TimeSpan.FromSeconds(10);
        var hosts = configuration.GetSection("AllowedHosts").Get<string[]>() is { Length: > 0 } h
            ? h.Select(x => x.Trim().ToLowerInvariant()).Where(x => x.Length > 0).ToArray()
            : DefaultAllowedHosts;

        if (string.IsNullOrWhiteSpace(pub) && string.IsNullOrWhiteSpace(priv))
            return new PushOptions { AllowedHosts = hosts, SendTimeout = timeout };

        // Messages name the setting, never the value.
        if (string.IsNullOrWhiteSpace(pub))
            throw new InvalidOperationException("Modules:Push:PublicKey is missing while PrivateKey is set.");
        if (string.IsNullOrWhiteSpace(priv))
            throw new InvalidOperationException("Modules:Push:PrivateKey is missing while PublicKey is set.");
        if (!VapidKeyGenerator.TryDecode(pub, 65, out var p) || p[0] != 0x04)
            throw new InvalidOperationException("Modules:Push:PublicKey is not a base64url uncompressed P-256 point (65 bytes).");
        if (!VapidKeyGenerator.TryDecode(priv, 32, out _))
            throw new InvalidOperationException("Modules:Push:PrivateKey is not a base64url P-256 private key (32 bytes).");
        if (string.IsNullOrWhiteSpace(subject)
            || !(subject.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
                 || subject.StartsWith("https:", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Modules:Push:Subject must be set to a mailto: or https: address when keys are set.");

        return new PushOptions { PublicKey = pub, PrivateKey = priv, Subject = subject, AllowedHosts = hosts, SendTimeout = timeout };
    }

    /// <summary>True for an absolute https URL on an allowed push service host: no IP literal,
    /// no userinfo, and no port other than 443.</summary>
    public bool IsAllowedEndpoint(string? endpoint)
    {
        if (string.IsNullOrEmpty(endpoint) || endpoint.Length >= 2048) return false;
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length > 0 || uri.Port != 443) return false;
        if (uri.HostNameType != UriHostNameType.Dns) return false;

        var host = uri.IdnHost.ToLowerInvariant().TrimEnd('.');
        foreach (var entry in AllowedHosts)
        {
            if (entry.StartsWith("*.", StringComparison.Ordinal)
                ? host.EndsWith(entry[1..], StringComparison.Ordinal) && host.Length > entry.Length - 1
                : host == entry)
                return true;
        }
        return false;
    }
}
