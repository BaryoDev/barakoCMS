using System.Diagnostics;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// A host that cannot start has to exit non-zero.
/// </summary>
/// <remarks>
/// Everything that decides whether a deploy worked reads the exit code: CI, a <c>docker run</c>
/// wrapper, systemd, a Kubernetes Job container. The top-level handler logged the fatal error and
/// then let the process end normally, so a schema mismatch, an unreachable database or a failed
/// migration all reported success.
///
/// This has to run the host as a real process. Exit code is a property of the process, and an
/// in-process host cannot demonstrate it: <c>WebApplicationFactory</c> catches what it starts, and
/// asserting on <c>Environment.ExitCode</c> from inside the test runner would assert on the runner's
/// own exit code, which is a test that cannot fail.
/// </remarks>
[Collection("Sequential")]
public class HostStartupExitCodeTests
{
    private static readonly string HostAssembly = typeof(barakoCMS.Extensions.ServiceCollectionExtensions).Assembly.Location;

    private readonly IntegrationTestFixture _fixture;

    public HostStartupExitCodeTests(IntegrationTestFixture fixture) => _fixture = fixture;

    /// <summary>
    /// No database configured, outside Development: the host stops and names the setting.
    /// </summary>
    /// <remarks>
    /// The old path substituted a localhost dummy connection string, so "nobody configured a
    /// database" arrived much later as a connection refused against 127.0.0.1 and never mentioned
    /// the setting that was missing. Asserting on the message as well as the code is deliberate: a
    /// non-zero exit for the wrong reason would otherwise pass.
    /// </remarks>
    [Fact]
    public async Task A_host_with_no_connection_string_exits_non_zero_and_names_the_setting()
    {
        var (exitCode, output) = await RunHostAsync(connectionString: null);

        exitCode.Should().NotBe(0);
        output.Should().Contain("DATABASE_URL");
    }

    /// <summary>
    /// A database it cannot reach: the host stops non-zero rather than shutting down cleanly.
    /// </summary>
    /// <remarks>
    /// This is the path through the top-level catch, which is the one that used to exit 0. Port 1 is
    /// refused immediately, so the failure is a connection error rather than a timeout and the test
    /// does not sit on a retry loop.
    /// </remarks>
    [Fact]
    public async Task A_host_that_cannot_reach_its_database_exits_non_zero()
    {
        var (exitCode, _) = await RunHostAsync(
            "Server=127.0.0.1;Port=1;Database=nope;User Id=postgres;Password=postgres;Timeout=2;Command Timeout=2");

        exitCode.Should().NotBe(0);
    }

    private static async Task<(int ExitCode, string Output)> RunHostAsync(string? connectionString)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add(HostAssembly);

        // The environment is built explicitly rather than inherited. IntegrationTestFixture sets
        // DATABASE_URL and ConnectionStrings__DefaultConnection on this process, so an inherited
        // environment would hand the child a working database and both cases would pass for the
        // wrong reason.
        start.Environment.Clear();
        start.Environment["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin";
        start.Environment["HOME"] = Environment.GetEnvironmentVariable("HOME") ?? "/tmp";
        start.Environment["DOTNET_ROOT"] = Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? string.Empty;
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        start.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";
        start.Environment["SKIP_SEEDER"] = "true";
        start.Environment["JWT__Key"] = "test-super-secret-key-that-is-at-least-32-chars-long";
        if (connectionString is not null)
        {
            start.Environment["ConnectionStrings__DefaultConnection"] = connectionString;
        }

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("The host did not exit; it started serving instead of failing.");
        }

        return (process.ExitCode, await stdout + await stderr);
    }

    /// <summary>
    /// A host started with an ASP.NET flag still creates its whole schema.
    /// </summary>
    /// <remarks>
    /// The schema commands are dispatched by JasperFx, which takes a bare first argument as a
    /// command name. Gating the boot-time schema apply on "were there any arguments" therefore
    /// looked right and was not: JasperFx detects reserved .NET and ASP.NET flags, bypasses its own
    /// command line and serves normally, so <c>--urls</c> produced a running host whose schema was
    /// whatever its first request happened to create on demand.
    ///
    /// The assertion is on a table nothing creates lazily during a health check. Counting tables, or
    /// asserting the host responds, both pass on the broken version.
    /// </remarks>
    [Fact]
    public async Task A_host_started_with_an_aspnet_flag_still_creates_its_schema()
    {
        var database = "flagstart_" + Guid.NewGuid().ToString("n")[..8];
        var admin = new NpgsqlConnectionStringBuilder(_fixture.ConnectionString);
        await using (var connection = new NpgsqlConnection(admin.ConnectionString))
        {
            await connection.OpenAsync();
            await using var create = new NpgsqlCommand($"create database {database}", connection);
            await create.ExecuteNonQueryAsync();
        }

        var target = new NpgsqlConnectionStringBuilder(_fixture.ConnectionString) { Database = database };
        var port = 58200 + Random.Shared.Next(1, 300);

        using var host = StartHost(target.ConnectionString, "--urls", $"http://127.0.0.1:{port}");
        try
        {
            await WaitForHealthAsync($"http://127.0.0.1:{port}/health", host);

            await using var check = new NpgsqlConnection(target.ConnectionString);
            await check.OpenAsync();
            await using var query = new NpgsqlCommand(
                "select count(*) from information_schema.tables where table_schema = 'public' and table_name = 'mt_doc_api_keys'",
                check);
            var found = Convert.ToInt64(await query.ExecuteScalarAsync());
            found.Should().Be(1, "the boot-time schema apply must run when the host is going to serve");
        }
        finally
        {
            if (!host.HasExited)
            {
                host.Kill(entireProcessTree: true);
            }
        }
    }

    private static Process StartHost(string connectionString, params string[] args)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add(HostAssembly);
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        start.Environment.Clear();
        start.Environment["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin";
        start.Environment["HOME"] = Environment.GetEnvironmentVariable("HOME") ?? "/tmp";
        start.Environment["DOTNET_ROOT"] = Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? string.Empty;
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        start.Environment["SKIP_SEEDER"] = "true";
        start.Environment["Kubernetes__Enabled"] = "false";
        start.Environment["JWT__Key"] = "test-super-secret-key-that-is-at-least-32-chars-long";
        start.Environment["ConnectionStrings__DefaultConnection"] = connectionString;

        return Process.Start(start)!;
    }

    private static async Task WaitForHealthAsync(string url, Process host)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        for (var attempt = 0; attempt < 60; attempt++)
        {
            if (host.HasExited)
            {
                throw new InvalidOperationException(
                    $"the host exited with {host.ExitCode}: {await host.StandardError.ReadToEndAsync()}");
            }

            try
            {
                using var response = await client.GetAsync(url);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException) { }

            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        throw new TimeoutException("the host never became healthy");
    }
}
