using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// The core host must not report itself ready before roles and the initial admin exist.
/// </summary>
/// <remarks>
/// It used to seed on a detached task that slept five seconds first, while app.Run() was already
/// accepting traffic. For at least those five seconds the system roles and the InitialAdmin user did
/// not exist, so the admin could not sign in and a registration landing in the window was stored
/// with an empty RoleIds, because Register skips the User role when it cannot find it. Under a
/// rolling deploy that repeated on every new node.
///
/// This has to run the host as a real process against an empty database. Both halves matter: the
/// readiness probe going healthy is what a load balancer waits for, and the login is what a client
/// does the moment traffic is routed. Asserting only that the seed eventually finishes would pass on
/// the broken version too.
///
/// See issue #256.
/// </remarks>
[Collection("Sequential")]
public class StartupSeedReadinessTests
{
    /// <summary>
    /// The core project's own build output, not the copy beside the test assembly. JasperFx scans
    /// every assembly in the host's directory at startup, and the xunit runner assemblies sitting
    /// next to the test copy make that scan throw before the host serves a single request.
    /// </summary>
    private static string HostAssembly()
    {
        var output = new DirectoryInfo(AppContext.BaseDirectory);
        var targetFramework = output.Name;
        var configuration = output.Parent!.Name;

        var repoRoot = output;
        while (repoRoot is not null && !File.Exists(Path.Combine(repoRoot.FullName, "Directory.Build.props")))
            repoRoot = repoRoot.Parent;
        repoRoot.Should().NotBeNull("the test must be able to find the repository root");

        return Path.Combine(repoRoot!.FullName, "barakoCMS", "bin", configuration, targetFramework, "barakoCMS.dll");
    }

    private const string AdminUsername = "seedgate_admin";
    private const string AdminPassword = "SeedGateAdminPassword123!";

    private readonly IntegrationTestFixture _fixture;

    public StartupSeedReadinessTests(IntegrationTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task The_admin_can_sign_in_on_the_first_request_accepted_after_readiness()
    {
        var connectionString = await CreateEmptyDatabaseAsync();
        var port = FreePort();

        using var host = StartHost(connectionString, port);
        var stdout = host.StandardOutput.ReadToEndAsync();
        var stderr = host.StandardError.ReadToEndAsync();

        try
        {
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

            var ready = await WaitForReadyAsync(client, host);
            if (!ready)
            {
                if (!host.HasExited)
                {
                    host.Kill(entireProcessTree: true);
                }

                throw new Xunit.Sdk.XunitException(
                    "the host never reported ready.\n"
                  + $"stdout:\n{Truncate(await stdout)}\nstderr:\n{Truncate(await stderr)}");
            }

            var login = await client.PostAsJsonAsync(
                "/api/auth/login",
                new { Username = AdminUsername, Password = AdminPassword });

            login.StatusCode.Should().Be(
                HttpStatusCode.OK,
                "readiness is the signal that traffic may be routed here, so the first request after "
              + "it must not find a system with no admin and no roles");
        }
        finally
        {
            if (!host.HasExited)
            {
                host.Kill(entireProcessTree: true);
            }

            await stdout;
            await stderr;
        }
    }

    private static string Truncate(string text) => text.Length <= 4000 ? text : text[^4000..];

    /// <summary>
    /// Polls until readiness reports healthy. A 503 is the expected answer while the seed runs, and
    /// a connection failure is the expected answer before the port binds.
    /// </summary>
    private static async Task<bool> WaitForReadyAsync(HttpClient client, Process host)
    {
        for (var attempt = 0; attempt < 180; attempt++)
        {
            if (host.HasExited)
            {
                return false;
            }

            try
            {
                var response = await client.GetAsync("/health/ready");
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return true;
                }
            }
            catch (HttpRequestException)
            {
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        return false;
    }

    private async Task<string> CreateEmptyDatabaseAsync()
    {
        var database = "seedgate_" + Guid.NewGuid().ToString("n")[..8];

        await using (var connection = new NpgsqlConnection(_fixture.ConnectionString))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var create = new NpgsqlCommand($"create database {database}", connection);
            await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        return new NpgsqlConnectionStringBuilder(_fixture.ConnectionString) { Database = database }
            .ConnectionString;
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static Process StartHost(string connectionString, int port)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add(HostAssembly());

        // Built explicitly, not inherited: the fixture sets DATABASE_URL on this process and the
        // child would take that over the empty database this test just made.
        start.Environment.Clear();
        start.Environment["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin";
        start.Environment["HOME"] = Environment.GetEnvironmentVariable("HOME") ?? "/tmp";
        start.Environment["DOTNET_ROOT"] = Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? string.Empty;
        // Development, so the host does not answer plain HTTP with an HTTPS redirect.
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        start.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        start.Environment["Kubernetes__Enabled"] = "false";
        start.Environment["Seed__DemoContent"] = "false";
        start.Environment["JWT__Key"] = "test-super-secret-key-that-is-at-least-32-chars-long";
        start.Environment["InitialAdmin__Username"] = AdminUsername;
        start.Environment["InitialAdmin__Password"] = AdminPassword;
        start.Environment["ConnectionStrings__DefaultConnection"] = connectionString;

        return Process.Start(start)!;
    }
}
