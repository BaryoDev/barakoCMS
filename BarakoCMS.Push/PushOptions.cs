using Microsoft.Extensions.Configuration;

namespace BarakoCMS.Push;

public sealed class PushOptions
{
    public string? PublicKey { get; init; }
    public string? PrivateKey { get; init; }
    public string? Subject { get; init; }

    public bool IsConfigured => PublicKey is not null && PrivateKey is not null;

    public static PushOptions From(IConfiguration configuration)
    {
        var pub = configuration["PublicKey"];
        var priv = configuration["PrivateKey"];
        var subject = configuration["Subject"]?.Trim();

        if (string.IsNullOrWhiteSpace(pub) && string.IsNullOrWhiteSpace(priv))
            return new PushOptions();

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

        return new PushOptions { PublicKey = pub, PrivateKey = priv, Subject = subject };
    }
}
