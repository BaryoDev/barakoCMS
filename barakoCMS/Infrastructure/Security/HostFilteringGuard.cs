namespace barakoCMS.Infrastructure.Security;

/// <summary>
/// Refuses to start outside Development when <c>AllowedHosts</c> accepts every host, unless the
/// operator says so on purpose with <c>Security:AllowAnyHost=true</c>.
/// </summary>
/// <remarks>
/// With <c>AllowedHosts</c> at <c>*</c> the <c>Host</c> header is whatever the caller wrote, so
/// links, tenant resolution and cache keys that read it follow the caller. A Kubernetes
/// <c>httpGet</c> probe sends the pod address as <c>Host</c>, so a probe needs a <c>Host</c> header
/// of its own once real hosts are listed. The check reuses
/// <see cref="CanonicalHost.IsHostFilteringConstrained"/>, so a wildcard subdomain such as
/// <c>*.example.com</c> counts as a real restriction.
/// </remarks>
internal static class HostFilteringGuard
{
    public const string AllowAnyHostKey = "Security:AllowAnyHost";

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> when host filtering is open outside Development
    /// and the operator has not allowed it. Calls <paramref name="warn"/> once when it is allowed.
    /// </summary>
    public static void Validate(IConfiguration configuration, bool isDevelopment, Action<string>? warn = null)
    {
        if (isDevelopment || CanonicalHost.IsHostFilteringConstrained(configuration[CanonicalHost.AllowedHostsKey]))
        {
            return;
        }

        if (configuration.GetValue<bool>(AllowAnyHostKey))
        {
            warn?.Invoke(
                $"{AllowAnyHostKey} is true, so this API answers to any Host header. Set "
              + $"{CanonicalHost.AllowedHostsKey} to the host names it serves instead.");
            return;
        }

        throw new InvalidOperationException(
            $"{CanonicalHost.AllowedHostsKey} is '*' or empty, which accepts any Host header. Set "
          + $"{CanonicalHost.AllowedHostsKey} to the host names this API answers to, for example "
          + $"{CanonicalHost.AllowedHostsKey}=api.example.com (environment variable AllowedHosts). "
          + $"To accept any host on purpose, set {AllowAnyHostKey}=true (environment variable Security__AllowAnyHost).");
    }
}
