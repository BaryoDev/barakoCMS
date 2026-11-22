using System.Net;
using System.Net.Sockets;
using System.Text;
using barakoCMS.Infrastructure.Health;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// <c>--health-probe</c> is the container healthcheck: the chiseled runtime image has no shell or
/// curl. Tested against a small listener in this process, not the whole host.
/// </summary>
public class HealthProbeCommandTests
{
    /// <summary>Answers each request with a fixed status line, or never answers, and records the request head.</summary>
    private sealed class FakeServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly bool _silent;
        private readonly string _status;
        private readonly string _body;

        public FakeServer(string status = "200 OK", bool silent = false, string body = "")
        {
            _status = status;
            _silent = silent;
            _body = body;
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(AcceptAsync);
        }

        public int Port { get; }

        public TaskCompletionSource<string> Request { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private async Task AcceptAsync()
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync();
                var stream = client.GetStream();
                var head = new StringBuilder();
                var buffer = new byte[1024];
                while (!head.ToString().Contains("\r\n\r\n"))
                {
                    var read = await stream.ReadAsync(buffer);
                    if (read == 0)
                    {
                        break;
                    }

                    head.Append(Encoding.ASCII.GetString(buffer, 0, read));
                }

                Request.TrySetResult(head.ToString());
                if (_silent)
                {
                    await Task.Delay(TimeSpan.FromSeconds(30));
                    return;
                }

                var response = $"HTTP/1.1 {_status}\r\nContent-Length: {_body.Length}\r\nX-Secret: hidden\r\nConnection: close\r\n\r\n{_body}";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(response));
            }
            catch (Exception ex) when (ex is ObjectDisposedException or IOException or SocketException)
            {
                // The test ended first.
            }
        }

        public void Dispose() => _listener.Stop();
    }

    private static Func<string, string?> Env(int port, string? allowedHosts = null, string? timeout = null) =>
        name => name switch
        {
            "ASPNETCORE_URLS" => $"http://+:{port}",
            "AllowedHosts" => allowedHosts,
            HealthProbe.TimeoutVariable => timeout,
            _ => null,
        };

    private static async Task<(int Exit, string Output)> RunAsync(
        string[] args, Func<string, string?> env, TimeSpan? timeout = null)
    {
        var output = new StringWriter();
        var exit = await HealthProbe.RunAsync(args, env, output, timeout);
        return (exit, output.ToString());
    }

    [Theory]
    [InlineData("200 OK")]
    [InlineData("204 No Content")]
    public async Task A_2xx_answer_exits_zero(string status)
    {
        using var server = new FakeServer(status);

        var (exit, _) = await RunAsync([HealthProbe.Flag], Env(server.Port));

        exit.Should().Be(0);
    }

    [Theory]
    [InlineData("500 Internal Server Error")]
    [InlineData("503 Service Unavailable")]
    [InlineData("400 Bad Request")]
    [InlineData("302 Found")]
    public async Task Any_other_answer_exits_one(string status)
    {
        using var server = new FakeServer(status);

        var (exit, _) = await RunAsync([HealthProbe.Flag], Env(server.Port));

        exit.Should().Be(1);
    }

    [Fact]
    public async Task A_refused_connection_exits_one()
    {
        int closedPort;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            closedPort = ((IPEndPoint)probe.LocalEndpoint).Port;
        }

        var (exit, _) = await RunAsync([HealthProbe.Flag], Env(closedPort));

        exit.Should().Be(1);
    }

    [Fact]
    public async Task A_server_that_never_answers_exits_one_within_the_timeout()
    {
        using var server = new FakeServer(silent: true);
        var started = DateTime.UtcNow;

        var (exit, _) = await RunAsync([HealthProbe.Flag], Env(server.Port), TimeSpan.FromMilliseconds(300));

        exit.Should().Be(1);
        (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task It_asks_for_the_live_path_unless_given_another()
    {
        using var live = new FakeServer();
        await RunAsync([HealthProbe.Flag], Env(live.Port));
        (await live.Request.Task).Should().StartWith("GET /health/live ");

        using var ready = new FakeServer();
        await RunAsync([HealthProbe.Flag, "/health/ready"], Env(ready.Port));
        (await ready.Request.Task).Should().StartWith("GET /health/ready ");
    }

    [Theory]
    [InlineData("health/ready")]
    [InlineData("/health ready")]
    public async Task A_path_that_is_not_an_absolute_path_exits_one_without_a_request(string path)
    {
        using var server = new FakeServer();

        var (exit, _) = await RunAsync([HealthProbe.Flag, path], Env(server.Port));

        exit.Should().Be(1);
        server.Request.Task.IsCompleted.Should().BeFalse();
    }

    [Theory]
    [InlineData("api.example.com;app", "api.example.com")]
    [InlineData("*;api.example.com", "api.example.com")]
    [InlineData("*.example.com; api.example.com", "api.example.com")]
    [InlineData("localhost;127.0.0.1", "localhost")]
    [InlineData("*", "localhost")]
    [InlineData("", "localhost")]
    [InlineData(null, "localhost")]
    public async Task The_first_entry_that_is_not_a_wildcard_is_sent_as_the_Host_header(string? allowedHosts, string expected)
    {
        // A deployment listing real host names answers any other Host header with a 400, so a probe
        // that sent its own address would report a healthy API as down.
        using var server = new FakeServer();

        await RunAsync([HealthProbe.Flag], Env(server.Port, allowedHosts));

        (await server.Request.Task).Should().Contain($"Host: {expected}\r\n");
    }

    [Theory]
    [InlineData("http://+:8080", 8080)]
    [InlineData("http://*:9090", 9090)]
    [InlineData("http://0.0.0.0:7000/", 7000)]
    [InlineData("http://[::]:7001", 7001)]
    [InlineData("https://+:8443;http://+:8081", 8081)]
    [InlineData("http://+:0", 8080)]
    [InlineData("http://+:notaport", 8080)]
    [InlineData("", 8080)]
    [InlineData(null, 8080)]
    public void The_port_comes_from_the_first_http_entry_of_ASPNETCORE_URLS(string? urls, int expected)
    {
        HealthProbe.PortFrom(urls).Should().Be(expected);
    }

    [Theory]
    [InlineData("5", 5)]
    [InlineData("60", 60)]
    [InlineData("0", HealthProbe.DefaultTimeoutSeconds)]
    [InlineData("-1", HealthProbe.DefaultTimeoutSeconds)]
    [InlineData("61", HealthProbe.DefaultTimeoutSeconds)]
    [InlineData("soon", HealthProbe.DefaultTimeoutSeconds)]
    [InlineData("", HealthProbe.DefaultTimeoutSeconds)]
    [InlineData(null, HealthProbe.DefaultTimeoutSeconds)]
    public void A_blank_or_malformed_timeout_keeps_the_default(string? configured, int expectedSeconds)
    {
        HealthProbe.TimeoutFrom(configured).Should().Be(TimeSpan.FromSeconds(expectedSeconds));
    }

    [Fact]
    public async Task It_prints_the_status_code_and_nothing_the_server_sent()
    {
        using var server = new FakeServer("200 OK", body: "internal-detail-from-the-body");

        var (_, output) = await RunAsync([HealthProbe.Flag], Env(server.Port));

        output.Should().Contain("200");
        output.Should().NotContain("internal-detail-from-the-body").And.NotContain("hidden");
    }

    [Fact]
    public void Only_a_first_argument_of_the_flag_selects_the_probe()
    {
        HealthProbe.IsProbe([HealthProbe.Flag]).Should().BeTrue();
        HealthProbe.IsProbe([HealthProbe.Flag, "/health/ready"]).Should().BeTrue();
        HealthProbe.IsProbe([]).Should().BeFalse();
        HealthProbe.IsProbe(["run", HealthProbe.Flag]).Should().BeFalse();
        HealthProbe.IsProbe(["--urls", "http://127.0.0.1:0"]).Should().BeFalse();
    }

    [Fact]
    public void The_probe_is_answered_before_the_web_host_is_built()
    {
        // The probe has to run on a container with no database, so nothing that builds the host,
        // reads configuration or starts the seeder may come ahead of it in Program.cs.
        var source = File.ReadAllText(Path.Combine(RepoPaths.Root, "barakoCMS", "Program.cs"));
        var probe = source.IndexOf("HealthProbe.RunAsync", StringComparison.Ordinal);

        probe.Should().BeGreaterThan(0);
        foreach (var setup in new[] { "WebApplication.CreateBuilder", "AddBarakoCMS", "DataSeeder", "ApplyMartenSchemaAsync", "RunBarakoCommandsAsync", "Log.Logger" })
        {
            source.IndexOf(setup, StringComparison.Ordinal).Should().BeGreaterThan(probe, "{0} must come after the probe", setup);
        }
    }
}
