using System.Net;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using barakoCMS.Infrastructure;
using barakoCMS.Infrastructure.Security;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// Behind a reverse proxy the socket peer is the proxy, so every client shared one rate-limit
/// bucket and every audit entry recorded the proxy's address.
/// </summary>
/// <remarks>
/// The half people forget is the second one. Reading X-Forwarded-For is easy; refusing to read it
/// from a peer nobody vouched for is the security property, because the header is client-supplied
/// and honouring it from anywhere lets a caller pick its own IP and walk straight past the per-IP
/// limit on /api/auth/login. Both directions are asserted here. See issue #263.
/// </remarks>
public class ForwardedHeadersTests
{
    private static IConfiguration Config(params (string Key, string Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

    private const string Proxy = "10.20.30.40";
    private const string Client = "203.0.113.7";
    private const string Stranger = "198.51.100.9";

    /// <summary>
    /// Runs one request through the real middleware with the real configuration reader and reports
    /// the client IP the rest of the pipeline would see, which is what the rate limiter partitions
    /// on and what DeviceContext records.
    /// </summary>
    private static async Task<string> ObservedClientIp(IConfiguration configuration, string peer, string? forwardedFor)
    {
        using var host = await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                    services.Configure<ForwardedHeadersOptions>(o => ForwardedHeadersSetup.Configure(o, configuration)));
                web.Configure(app =>
                {
                    app.Use(async (ctx, next) =>
                    {
                        ctx.Connection.RemoteIpAddress = IPAddress.Parse(peer);
                        await next();
                    });
                    app.UseForwardedHeaders();
                    app.Run(ctx => ctx.Response.WriteAsync(DeviceContext.From(ctx).IpAddress));
                });
            })
            .StartAsync();

        var client = host.GetTestClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/");
        if (forwardedFor is not null)
            request.Headers.Add("X-Forwarded-For", forwardedFor);

        var response = await client.SendAsync(request);
        return await response.Content.ReadAsStringAsync();
    }

    private static IConfiguration TrustingTheProxy => Config(
        ("ForwardedHeaders:Enabled", "true"),
        ("ForwardedHeaders:KnownProxies:0", Proxy));

    [Fact]
    public async Task The_client_ip_comes_from_the_forwarded_header_when_the_proxy_is_trusted()
    {
        var observed = await ObservedClientIp(TrustingTheProxy, peer: Proxy, forwardedFor: Client);

        observed.Should().Be(Client,
            "the request arrived from the proxy named in KnownProxies, so its X-Forwarded-For is the "
          + "only evidence of who the client is");
    }

    [Fact]
    public async Task The_client_ip_is_not_taken_from_a_forwarded_header_sent_by_an_untrusted_peer()
    {
        var observed = await ObservedClientIp(TrustingTheProxy, peer: Stranger, forwardedFor: Client);

        observed.Should().Be(Stranger,
            "nothing vouches for this peer, so its X-Forwarded-For is just a string it chose. "
          + "Honouring it would let any caller pick the IP that rate limiting keys on and the one "
          + "the audit log records");
    }

    [Fact]
    public async Task A_trusted_network_covers_every_proxy_in_its_range()
    {
        var configuration = Config(
            ("ForwardedHeaders:Enabled", "true"),
            ("ForwardedHeaders:KnownNetworks:0", "10.20.0.0/16"));

        var observed = await ObservedClientIp(configuration, peer: Proxy, forwardedFor: Client);

        observed.Should().Be(Client, "10.20.30.40 is inside 10.20.0.0/16");
    }

    [Fact]
    public async Task A_peer_outside_every_trusted_network_is_still_the_client_ip()
    {
        var configuration = Config(
            ("ForwardedHeaders:Enabled", "true"),
            ("ForwardedHeaders:KnownNetworks:0", "10.20.0.0/16"));

        var observed = await ObservedClientIp(configuration, peer: Stranger, forwardedFor: Client);

        observed.Should().Be(Stranger, "198.51.100.9 is outside 10.20.0.0/16");
    }

    [Fact]
    public async Task Only_the_hop_the_trusted_proxy_saw_is_read()
    {
        var observed = await ObservedClientIp(TrustingTheProxy, peer: Proxy, forwardedFor: $"{Stranger}, {Client}");

        observed.Should().Be(Client,
            "the trusted proxy appends the peer it saw, so the rightmost entry is the only one it "
          + "wrote. Everything to its left was supplied by the caller");
    }

    [Fact]
    public void Turning_the_feature_on_without_naming_a_proxy_fails_at_startup()
    {
        var configuration = Config(("ForwardedHeaders:Enabled", "true"));

        var configure = () => ForwardedHeadersSetup.Configure(new ForwardedHeadersOptions(), configuration);

        configure.Should().Throw<InvalidOperationException>(
            "an empty trusted set is the trap this exists to avoid: it either does nothing or trusts "
          + "every upstream, and both look like working configuration")
            .WithMessage("*KnownProxies*");
    }

    [Fact]
    public void An_unparseable_proxy_address_fails_at_startup()
    {
        var configuration = Config(
            ("ForwardedHeaders:Enabled", "true"),
            ("ForwardedHeaders:KnownProxies:0", "caddy"));

        var configure = () => ForwardedHeadersSetup.Configure(new ForwardedHeadersOptions(), configuration);

        configure.Should().Throw<InvalidOperationException>(
            "a hostname silently skipped would leave the trusted set empty while the operator "
          + "believed the proxy was named");
    }

    [Fact]
    public void The_feature_is_off_unless_configuration_turns_it_on()
    {
        ForwardedHeadersSetup.IsEnabled(Config()).Should().BeFalse(
            "trusting nothing is the failing-closed default; a deployment behind a proxy opts in and "
          + "says which proxy");
    }

    /// <summary>
    /// The trust the shipped production compose file configures when .env sets nothing (#651).
    /// </summary>
    /// <remarks>
    /// It used to be 172.16.0.0/12, every address a default Docker bridge hands out, so any other
    /// container that could reach app:8080 was trusted and could write the client IP. The defaults
    /// are read from the file rather than restated here, so the test follows the file.
    /// </remarks>
    private static IConfiguration ProductionComposeDefaults()
    {
        var settings = new List<(string, string)>();
        foreach (var line in ProductionComposeLines())
        {
            var match = Regex.Match(line, @"^-\s*ForwardedHeaders__(?<key>\w+)=(?<value>.*)$");
            if (match.Success)
                settings.Add(($"ForwardedHeaders:{match.Groups["key"].Value.Replace("__", ":")}", ResolveDefaults(match.Groups["value"].Value)));
        }

        settings.Should().Contain(s => s.Item1 == "ForwardedHeaders:Enabled",
            "docker-compose.prod.yml turns forwarded headers on for Caddy");
        return Config(settings.ToArray());
    }

    private static string[] ProductionComposeLines() =>
        File.ReadAllLines(Path.Combine(ComposeDefaultsTests.RepoRoot(), "docker-compose.prod.yml"))
            .Select(l => l.Trim())
            .Where(l => !l.StartsWith('#'))
            .ToArray();

    /// <summary>Resolves ${VAR:-default} the way compose does when VAR is unset, innermost first.</summary>
    private static string ResolveDefaults(string value)
    {
        var variable = new Regex(@"\$\{[A-Za-z_][A-Za-z0-9_]*(?::?-(?<default>[^${}]*))?\}");
        while (variable.IsMatch(value))
            value = variable.Replace(value, m => m.Groups["default"].Value);
        return value;
    }

    private static string? CaddyAddressInProductionCompose() =>
        ProductionComposeLines()
            .Where(l => l.StartsWith("ipv4_address:", StringComparison.Ordinal))
            .Select(l => ResolveDefaults(l["ipv4_address:".Length..].Trim()))
            .SingleOrDefault();

    [Fact]
    public async Task The_production_compose_trusts_a_forwarded_header_from_caddy()
    {
        var caddy = CaddyAddressInProductionCompose();
        caddy.Should().NotBeNull("Caddy needs a fixed address for the app to trust that address alone");

        var observed = await ObservedClientIp(ProductionComposeDefaults(), peer: caddy!, forwardedFor: Client);

        observed.Should().Be(Client, "the request came from Caddy, the one proxy the stack has");
    }

    [Fact]
    public async Task The_production_compose_trusts_caddy_when_kestrel_reports_an_ipv4_mapped_address()
    {
        var caddy = CaddyAddressInProductionCompose();
        caddy.Should().NotBeNull("Caddy needs a fixed address for the app to trust that address alone");

        var observed = await ObservedClientIp(ProductionComposeDefaults(), peer: $"::ffff:{caddy}", forwardedFor: Client);

        observed.Should().Be(Client,
            "Kestrel listens dual stack in the container, so an IPv4 peer can arrive as ::ffff:a.b.c.d");
    }

    [Theory]
    [InlineData("172.18.0.5")]
    [InlineData("172.17.0.9")]
    [InlineData("10.87.51.3")]
    public async Task The_production_compose_ignores_a_forwarded_header_from_another_container(string neighbour)
    {
        var observed = await ObservedClientIp(ProductionComposeDefaults(), peer: neighbour, forwardedFor: Client);

        observed.Should().Be(neighbour,
            "{0} is another container on a Docker network, not Caddy. Honouring its X-Forwarded-For "
          + "would let it pick the IP the audit log records and the rate limiter keys on", neighbour);
    }

    [Fact]
    public void DeviceContext_does_not_read_the_forwarded_header_itself()
    {
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Parse(Stranger);
        ctx.Request.Headers["X-Forwarded-For"] = Client;

        DeviceContext.From(ctx).IpAddress.Should().Be(Stranger,
            "the audit IP is whatever the middleware left on the connection. Parsing the raw header "
          + "here would put a caller-chosen address in the audit log no matter how the middleware "
          + "is configured");
    }
}
