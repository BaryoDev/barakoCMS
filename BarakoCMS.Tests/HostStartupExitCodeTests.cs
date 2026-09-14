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

    private static readonly string SuiteAssembly = SuiteAssemblyPath();

    private const string UnreachableDatabase =
        "Server=127.0.0.1;Port=1;Database=nope;User Id=postgres;Password=postgres;Timeout=2;Command Timeout=2";

    private const string ValidJwtKey = "test-super-secret-key-that-is-at-least-32-chars-long";

    public static TheoryData<string, string> MissingSettings() => new()
    {
        { "core", "database" },
        { "core", "jwt" },
        { "suite", "database" },
        { "suite", "jwt" },
    };

    /// <summary>
    /// A required setting missing, outside Development: the host exits 1 and names the setting.
    /// </summary>
    /// <remarks>
    /// Exactly 1, not merely non-zero. Both settings throw inside <c>AddBarakoCMS</c>, and while that
    /// ran outside the top-level handler the exception reached the runtime, which aborts: exit 134
    /// here, where the test runner is the parent. In the published image the host is PID 1, the
    /// kernel does not deliver that abort to it, and the container logged the error and kept running
    /// (#763). A non-zero assertion passed on the abort. Exit 1 is only reachable through the handler.
    ///
    /// The Suite is covered because it is the host the image runs. Asserting on the message as well
    /// as the code is deliberate: a failure for the wrong reason would otherwise pass.
    /// </remarks>
    [Theory]
    [MemberData(nameof(MissingSettings))]
    public async Task A_host_missing_a_required_setting_exits_1_and_names_the_setting(string host, string missing)
    {
        var assembly = host == "suite" ? SuiteAssembly : HostAssembly;
        File.Exists(assembly).Should().BeTrue($"the {host} host must be built at {assembly}");

        var (exitCode, output) = await RunHostAsync(
            assembly,
            connectionString: missing == "database" ? null : UnreachableDatabase,
            jwtKey: missing == "jwt" ? null : ValidJwtKey);

        exitCode.Should().Be(1, $"an unhandled startup exception aborts instead (134), which PID 1 in a container never completes. Output:\n{Truncate(output)}");
        output.Should().Contain(missing == "database" ? "DATABASE_URL" : "JWT:Key");
    }

    /// <summary>
    /// Where the Suite host was built, taken from this assembly's own output path so the
    /// configuration and framework folders match.
    /// </summary>
    private static string SuiteAssemblyPath()
    {
        var output = AppContext.BaseDirectory;
        var root = new DirectoryInfo(output);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Directory.Build.props")))
            root = root.Parent;
        if (root is null)
            return "BarakoCMS.Suite.dll (repository root not found)";

        var relative = Path.GetRelativePath(Path.Combine(root.FullName, "BarakoCMS.Tests"), output);
        return Path.Combine(root.FullName, "BarakoCMS.Suite", relative, "BarakoCMS.Suite.dll");
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
        var (exitCode, _) = await RunHostAsync(HostAssembly, UnreachableDatabase, ValidJwtKey);

        exitCode.Should().NotBe(0);
    }

    /// <summary>
    /// An invalid rate limit stops the host with exit 1 and names the setting, rather than booting
    /// with the limit removed.
    /// </summary>
    [Fact]
    public async Task A_host_with_an_invalid_rate_limit_exits_1_and_names_the_setting()
    {
        var (exitCode, output) = await RunHostAsync(
            HostAssembly, UnreachableDatabase, ValidJwtKey,
            new Dictionary<string, string> { ["RateLimiting__Auth__PermitLimit"] = "0" });

        exitCode.Should().Be(1, $"Output:\n{Truncate(output)}");
        output.Should().Contain("RateLimiting:Auth:PermitLimit");
    }

    private static async Task<(int ExitCode, string Output)> RunHostAsync(
        string hostAssembly, string? connectionString, string? jwtKey,
        IReadOnlyDictionary<string, string>? extraEnvironment = null)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add(hostAssembly);

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
        if (jwtKey is not null)
        {
            start.Environment["JWT__Key"] = jwtKey;
        }
        if (connectionString is not null)
        {
            start.Environment["ConnectionStrings__DefaultConnection"] = connectionString;
        }
        foreach (var (name, value) in extraEnvironment ?? new Dictionary<string, string>())
        {
            start.Environment[name] = value;
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

        using var host = StartHost(target.ConnectionString, "--urls", "http://127.0.0.1:0");

        // Both pipes are drained from the start. A child whose output fills the 64KB pipe buffer
        // blocks on the write, and this host is chatty at startup.
        var output = host.StandardOutput.ReadToEndAsync();
        var errors = host.StandardError.ReadToEndAsync();

        try
        {
            // The assertion is on the database, not on the host answering. Whether the process is
            // still serving is a property of the environment (port binding, process lifetime,
            // signals); whether the schema was applied is the thing this test is about, and it stays
            // true after the host has gone.
            if (!await WaitForTableAsync(target.ConnectionString, "mt_doc_api_keys", host))
            {
                // Read the pipes only once the process is gone. Awaiting them while it is still
                // running never returns, and FluentAssertions evaluates a message argument whether
                // or not the assertion fails.
                var exit = host.HasExited ? host.ExitCode.ToString() : "still running";
                if (!host.HasExited)
                {
                    host.Kill(entireProcessTree: true);
                }

                throw new Xunit.Sdk.XunitException(
                    "the boot-time schema apply must run when the host is going to serve. "
                    + $"host exit: {exit}\nstdout:\n{Truncate(await output)}\nstderr:\n{Truncate(await errors)}");
            }
        }
        finally
        {
            if (!host.HasExited)
            {
                host.Kill(entireProcessTree: true);
            }
        }
    }

    private static string Truncate(string text) =>
        text.Length <= 4000 ? text : text[^4000..];

    /// <summary>
    /// Polls for a table that only the explicit schema apply creates. Nothing creates
    /// <c>mt_doc_api_keys</c> lazily during startup, so its presence means the apply ran and its
    /// absence means it did not. Counting tables would not do: a handful appear on demand.
    /// </summary>
    private static async Task<bool> WaitForTableAsync(string connectionString, string table, Process host)
    {
        for (var attempt = 0; attempt < 60; attempt++)
        {
            await using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync();
                await using var query = new NpgsqlCommand(
                    "select count(*) from information_schema.tables where table_schema = 'public' and table_name = @table",
                    connection);
                query.Parameters.AddWithValue("table", table);
                if (Convert.ToInt64(await query.ExecuteScalarAsync()) == 1)
                {
                    return true;
                }
            }

            // One more look after the process is gone, so a host that applied the schema and then
            // exited is not read as a failure by a race.
            if (host.HasExited)
            {
                await Task.Delay(TimeSpan.FromSeconds(1));
                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync();
                await using var query = new NpgsqlCommand(
                    "select count(*) from information_schema.tables where table_schema = 'public' and table_name = @table",
                    connection);
                query.Parameters.AddWithValue("table", table);
                return Convert.ToInt64(await query.ExecuteScalarAsync()) == 1;
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        return false;
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

}
