using System.Net;

namespace barakoCMS.Infrastructure.Health;

/// <summary>
/// <c>dotnet barakoCMS.dll --health-probe [path]</c>: asks the API running in this container whether
/// it is up, and exits 0 on a 2xx and 1 on anything else.
/// </summary>
/// <remarks>
/// The runtime image is chiseled, so it has no shell, no curl and no wget, and a compose
/// <c>healthcheck</c> has nothing else to run. The probe runs before the web host is built, so it
/// starts fast and never touches the database, the seeder or a migration. It prints the status code
/// and nothing a server sent back: no header, no body.
/// <para>
/// Host filtering: the request carries the first non-wildcard entry of the <c>AllowedHosts</c>
/// environment variable as its <c>Host</c> header, because a deployment that lists real host names
/// answers anything else with a 400. Without one it sends <c>localhost</c>.
/// </para>
/// </remarks>
internal static class HealthProbe
{
    public const string Flag = "--health-probe";
    public const string DefaultPath = "/health/live";
    public const int DefaultPort = 8080;
    public const int DefaultTimeoutSeconds = 3;

    /// <summary>Names the probe's timeout in seconds. Anything outside 1 to 60, or not a number, means the default.</summary>
    public const string TimeoutVariable = "BARAKO_HEALTH_PROBE_TIMEOUT_SECONDS";

    private const int MaxTimeoutSeconds = 60;

    public static bool IsProbe(string[] args) => args.Length > 0 && args[0] == Flag;

    /// <summary>Runs the probe and returns the process exit code.</summary>
    /// <param name="args">The command line, starting with the flag and then an optional path.</param>
    /// <param name="environment">Reads an environment variable.</param>
    /// <param name="output">Where the one status line goes.</param>
    /// <param name="timeout">Overrides the timeout, for a test.</param>
    public static async Task<int> RunAsync(
        string[] args, Func<string, string?> environment, TextWriter output, TimeSpan? timeout = null)
    {
        var path = args.Length > 1 ? args[1] : DefaultPath;
        if (!path.StartsWith('/') || path.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            output.WriteLine("health probe: the path must start with / and hold no spaces");
            return 1;
        }

        var url = $"http://127.0.0.1:{PortFrom(environment("ASPNETCORE_URLS"))}{path}";
        using var cts = new CancellationTokenSource(timeout ?? TimeoutFrom(environment(TimeoutVariable)));
        try
        {
            // No proxy and no redirects: the answer has to come from this process on loopback.
            using var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false };
            using var client = new HttpClient(handler);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Host = HostHeaderFrom(environment("AllowedHosts"));

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            var status = (int)response.StatusCode;
            output.WriteLine($"health probe: {path} answered {status}");
            return response.StatusCode is >= HttpStatusCode.OK and < HttpStatusCode.MultipleChoices ? 0 : 1;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or UriFormatException)
        {
            output.WriteLine($"health probe: {path} did not answer ({ex.GetType().Name})");
            return 1;
        }
    }

    /// <summary>
    /// The port of the first <c>ASPNETCORE_URLS</c> entry, or 8080. The host part is ignored: the
    /// probe always asks loopback, and a wildcard such as <c>+</c> or <c>*</c> includes it.
    /// </summary>
    internal static int PortFrom(string? urls)
    {
        var first = urls?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(u => !u.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
        if (first is null)
        {
            return DefaultPort;
        }

        var schemeEnd = first.IndexOf("://", StringComparison.Ordinal);
        var authority = (schemeEnd >= 0 ? first[(schemeEnd + 3)..] : first).Split('/', 2)[0];
        var colon = authority.LastIndexOf(':');
        return colon >= 0 && int.TryParse(authority[(colon + 1)..], out var port) && port is > 0 and <= 65535
            ? port
            : DefaultPort;
    }

    /// <summary>The first <c>AllowedHosts</c> entry that is not a wildcard, or <c>localhost</c>.</summary>
    internal static string HostHeaderFrom(string? allowedHosts) =>
        allowedHosts?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(h => !h.Contains('*') && h != "0.0.0.0" && h != "[::]" && !h.Any(char.IsWhiteSpace))
        ?? "localhost";

    internal static TimeSpan TimeoutFrom(string? configured) =>
        int.TryParse(configured, out var seconds) && seconds is >= 1 and <= MaxTimeoutSeconds
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.FromSeconds(DefaultTimeoutSeconds);
}
