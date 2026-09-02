using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.RegularExpressions;
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
        var (connectionString, database) = await CreateEmptyDatabaseAsync();
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

            // After the host has exited, so nothing is still holding a connection to it. This also
            // covers a StartHost that never became ready, which is the path that used to leak.
            await DropDatabaseAsync(database);
        }
    }

    private static string Truncate(string text) => text.Length <= 4000 ? text : text[^4000..];

    /// <summary>The line the seeder prints when it had to make a password up.</summary>
    private static readonly Regex GeneratedPassword =
        new(@"so one was generated for '[^']+': (?<password>\S+)", RegexOptions.Compiled);

    /// <summary>
    /// A host given no InitialAdmin password seeds one it generated and prints it once (#271).
    /// </summary>
    /// <remarks>
    /// The compose files used to default the password to the literal "changeme-in-production", so
    /// `docker compose up` with no .env produced a SuperAdmin whose password is in this repository.
    /// Removing the default alone would have been worse than the defect: the seeder only created the
    /// account when a password was configured, so an empty variable meant a first-run stack with no
    /// admin and no way in.
    ///
    /// End to end against a real host rather than against the generator, because three separate
    /// things have to line up and only the running system shows all three: the account is created,
    /// the password that reaches the log is the one BCrypt stored, and it satisfies the password
    /// policy so the account can later change it. A unit test on the generator proves none of that.
    /// </remarks>
    [Fact]
    public async Task An_unset_admin_password_is_generated_printed_once_and_signs_in()
    {
        var (connectionString, database) = await CreateEmptyDatabaseAsync();
        var port = FreePort();

        using var host = StartHost(connectionString, port, adminPassword: null);
        var stdout = new ConcurrentQueue<string>();
        var pump = PumpAsync(host.StandardOutput, stdout);
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

                var out1 = Truncate(Joined(stdout));
                var err1 = Truncate(await stderr);
                throw new Xunit.Sdk.XunitException(
                    "the host never reported ready.\n"
                  + $"stdout:\n{out1}\nstderr:\n{err1}");
            }

            // Readiness is after seeding, so the line has been written to the pipe by now. The pump
            // reading it into this process can still lag by a scheduling slice.
            var log = await WaitForLogAsync(stdout, GeneratedPassword);

            var match = GeneratedPassword.Match(log);
            match.Success.Should().BeTrue(
                "a host given no InitialAdmin password has to say what it generated, or the account "
              + "it just created is one nobody can sign in as. Log was:\n{0}", Truncate(log));

            var password = match.Groups["password"].Value;
            password.Should().NotBe("changeme-in-production",
                "the whole point is that the password is not one shipped in this repository");

            Regex.Matches(log, Regex.Escape(password)).Count.Should().Be(1,
                "it is printed once, on purpose. A credential repeated on every boot is one an "
              + "operator stops treating as a secret");

            var login = await client.PostAsJsonAsync(
                "/api/auth/login",
                new { Username = AdminUsername, Password = password });

            login.StatusCode.Should().Be(
                HttpStatusCode.OK,
                "the printed password must be the one that was hashed and stored, otherwise a stack "
              + "brought up with no .env has an admin account and no way into it");

            // The control. Without it the assertion above passes on a host that accepts anything.
            var wrong = await client.PostAsJsonAsync(
                "/api/auth/login",
                new { Username = AdminUsername, Password = "changeme-in-production" });

            wrong.StatusCode.Should().NotBe(HttpStatusCode.OK,
                "the old shipped default must not be what the account was seeded with");
        }
        finally
        {
            if (!host.HasExited)
            {
                host.Kill(entireProcessTree: true);
            }

            await pump;
            await stderr;
            await DropDatabaseAsync(database);
        }
    }

    private static async Task PumpAsync(StreamReader reader, ConcurrentQueue<string> into)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            into.Enqueue(line);
        }
    }

    /// <summary>Waits for the pump to have carried a line matching <paramref name="pattern"/>.</summary>
    private static async Task<string> WaitForLogAsync(ConcurrentQueue<string> stdout, Regex pattern)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var log = Joined(stdout);
            if (pattern.IsMatch(log))
            {
                return log;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        return Joined(stdout);
    }

    private static string Joined(ConcurrentQueue<string> lines) => string.Join(Environment.NewLine, lines);

    /// <summary>An empty home directory, so the child host inherits no developer configuration.</summary>
    private static string CleanHome()
    {
        var home = Path.Combine(Path.GetTempPath(), "barako_seedgate_home");
        Directory.CreateDirectory(home);
        return home;
    }


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

    private async Task<(string ConnectionString, string Database)> CreateEmptyDatabaseAsync()
    {
        var database = "seedgate_" + Guid.NewGuid().ToString("n")[..8];

        await using (var connection = new NpgsqlConnection(_fixture.ConnectionString))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var create = new NpgsqlCommand($"create database {database}", connection);
            await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        return (new NpgsqlConnectionStringBuilder(_fixture.ConnectionString) { Database = database }
            .ConnectionString, database);
    }

    /// <summary>
    /// Drops the database this test created.
    /// </summary>
    /// <remarks>
    /// Every run left one behind, named after a fresh guid, so nothing ever reused or cleaned them
    /// and a CI machine accumulated one per run forever. WITH (FORCE) because the host process may
    /// still be releasing its pool as this runs, and a drop that loses a race to a dying connection
    /// would fail the test for a reason that has nothing to do with what it asserts.
    /// </remarks>
    private async Task DropDatabaseAsync(string database)
    {
        try
        {
            await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var drop = new NpgsqlCommand(
                $"drop database if exists {database} with (force)", connection);
            await drop.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        catch (PostgresException)
        {
            // Cleanup, not an assertion. A test that already passed must not fail here.
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static Process StartHost(string connectionString, int port, string? adminPassword = AdminPassword)
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
        // A home of its own, not the developer's. The host runs in Development, and Development is
        // where the configuration builder adds user secrets, which live under $HOME and rank above
        // appsettings.json. On the machine this was written on that file sets InitialAdmin:Password,
        // so the unset-password test read a value nobody had configured for it and reported that the
        // seeder had not generated one. Any test here that does not set a variable is asserting on
        // the absence of it, and an inherited home makes that assertion about the machine.
        start.Environment["HOME"] = CleanHome();
        start.Environment["DOTNET_ROOT"] = Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? string.Empty;
        // Development, so the host does not answer plain HTTP with an HTTPS redirect.
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        start.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        start.Environment["Kubernetes__Enabled"] = "false";
        start.Environment["Seed__DemoContent"] = "false";
        start.Environment["JWT__Key"] = "test-super-secret-key-that-is-at-least-32-chars-long";
        start.Environment["InitialAdmin__Username"] = AdminUsername;
        // Left out entirely when null, rather than set to empty. The point of the unset case is a
        // host that was given no password at all.
        if (adminPassword is not null)
        {
            start.Environment["InitialAdmin__Password"] = adminPassword;
        }
        start.Environment["ConnectionStrings__DefaultConnection"] = connectionString;

        return Process.Start(start)!;
    }
}
