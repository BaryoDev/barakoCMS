using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;

namespace barakoCMS.Infrastructure.Security;

/// <summary>
/// Reads the <c>ForwardedHeaders</c> configuration section into <see cref="ForwardedHeadersOptions"/>.
/// </summary>
/// <remarks>
/// <para>
/// Behind a reverse proxy the socket peer is the proxy, so rate limiting and audit IPs key on one
/// address for every client. The cure is <c>X-Forwarded-For</c>, but that header is client-supplied:
/// honouring it from an untrusted peer lets a caller pick its own IP and defeat the per-IP limits
/// that this is meant to fix. The header is only worth reading when the hop it came from is known.
/// </para>
/// <para>
/// So the feature is off unless <c>ForwardedHeaders:Enabled</c> is true, and turning it on without
/// naming a proxy is a startup failure rather than a silent "trust everyone". The framework's own
/// defaults trust loopback, which is wrong in both directions here: in a container the proxy is a
/// peer on a bridge network rather than loopback, so the default would do nothing while looking as
/// though it did something. Both lists are cleared and rebuilt from configuration.
/// </para>
/// </remarks>
internal static class ForwardedHeadersSetup
{
    public const string Section = "ForwardedHeaders";

    public static bool IsEnabled(IConfiguration configuration) =>
        configuration.GetValue<bool>($"{Section}:Enabled");

    public static void Configure(ForwardedHeadersOptions options, IConfiguration configuration)
    {
        var section = configuration.GetSection(Section);

        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();

        foreach (var proxy in Values(section, "KnownProxies"))
        {
            if (!IPAddress.TryParse(proxy, out var address))
            {
                throw new InvalidOperationException(
                    $"{Section}:KnownProxies contains '{proxy}', which is not an IP address.");
            }

            options.KnownProxies.Add(address);
        }

        foreach (var network in Values(section, "KnownNetworks"))
        {
            if (!System.Net.IPNetwork.TryParse(network, out var parsed))
            {
                throw new InvalidOperationException(
                    $"{Section}:KnownNetworks contains '{network}', which is not CIDR notation such as 10.0.0.0/8.");
            }

            options.KnownIPNetworks.Add(parsed);
        }

        // One hop by default. The proxy appends the peer it saw, so anything further left in the
        // chain was written by something upstream of the trusted hop and is not evidence.
        options.ForwardLimit = section.GetValue<int?>("ForwardLimit") ?? 1;

        if (options.KnownProxies.Count == 0 && options.KnownIPNetworks.Count == 0)
        {
            throw new InvalidOperationException(
                $"{Section}:Enabled is true but neither {Section}:KnownProxies nor {Section}:KnownNetworks "
              + "names a trusted hop. Forwarded headers from an unnamed peer are client-controlled, so "
              + "reading them would let any caller choose the IP that rate limiting and the audit log see. "
              + "Set the proxy's address, or leave the feature off.");
        }
    }

    private static IEnumerable<string> Values(IConfigurationSection section, string key) =>
        section.GetSection(key)
            .GetChildren()
            .Select(c => c.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!.Trim());
}
