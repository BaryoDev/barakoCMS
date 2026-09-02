using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using barakoCMS.Infrastructure.Health;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// Liveness and readiness have to be different questions.
/// </summary>
/// <remarks>
/// Both probes pointed at <c>/health</c>, which runs every check including the database one. So a
/// Postgres restart failed liveness on every replica at once and Kubernetes killed a whole
/// deployment of healthy application processes, turning a database blip into an application outage
/// plus a cold-start stampede. The <c>ready</c> tag was already on the database check and no
/// endpoint filtered on it.
///
/// The interesting assertion is the negative one, that a failing database check leaves
/// <c>/health/live</c> alone. It is paired with a check tagged <c>live</c> that can fail, because a
/// liveness endpoint that runs nothing at all would pass the negative test on its own. Note that
/// UseHealthChecks maps a path prefix, so a single <c>/health</c> mapping answers
/// <c>/health/live</c> too: reverting the split does not 404 here, it returns the full report.
///
/// See issues #281 and #256.
/// </remarks>
[Collection("Sequential")]
public class HealthProbeTests
{
    private readonly IntegrationTestFixture _fixture;

    public HealthProbeTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private sealed class SwitchableCheck : IHealthCheck
    {
        public bool IsHealthy { get; set; } = true;

        public Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(IsHealthy
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("switched off by the test"));
    }

    private static readonly SwitchableCheck FakeDatabase = new();
    private static readonly SwitchableCheck FakeProcess = new();
    private static readonly object HostGate = new();
    private static WebApplicationFactory<Program>? _host;

    /// <summary>
    /// One host for the whole class. Building it registers two checks the test can switch off: one
    /// carrying the database tags, one carrying the liveness tag. Never disposed, per the note on
    /// IntegrationTestFixture.WithSetting.
    /// </summary>
    private WebApplicationFactory<Program> ProbeHost()
    {
        lock (HostGate)
        {
            _host ??= _fixture.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
                services.AddHealthChecks()
                    .AddCheck("TestDatabase", FakeDatabase, tags: new[] { "db", "ready" })
                    .AddCheck("TestProcess", FakeProcess, tags: new[] { "live" })));
        }

        FakeDatabase.IsHealthy = true;
        FakeProcess.IsHealthy = true;
        return _host;
    }

    private HttpClient ProbeClient() => ProbeHost().CreateClient();

    private static async Task<HttpStatusCode> Get(HttpClient client, string path) =>
        (await client.GetAsync(path, TestContext.Current.CancellationToken)).StatusCode;

    [Fact]
    public async Task A_failing_database_check_fails_readiness_and_leaves_liveness_healthy()
    {
        var client = ProbeClient();

        (await Get(client, "/health/live")).Should().Be(Expect(healthy: true),
            "the positive control: with nothing failing, liveness is healthy");
        (await Get(client, "/health/ready")).Should().Be(Expect(healthy: true),
            "the positive control: with nothing failing, readiness is healthy");

        FakeDatabase.IsHealthy = false;

        (await Get(client, "/health/ready")).Should().Be(Expect(healthy: false),
            "a pod that cannot reach its database must come out of the Service");
        (await Get(client, "/health")).Should().Be(Expect(healthy: false),
            "the full report still shows the failure");
        (await Get(client, "/health/live")).Should().Be(Expect(healthy: true),
            "restarting the process does not bring Postgres back, and a shared database means "
          + "every replica fails this probe at the same moment");
    }

    [Fact]
    public async Task A_failing_process_check_fails_liveness_and_leaves_readiness_healthy()
    {
        var client = ProbeClient();

        FakeProcess.IsHealthy = false;

        (await Get(client, "/health/live")).Should().Be(Expect(healthy: false),
            "liveness has to be able to fail, or the negative test above proves nothing");
        (await Get(client, "/health/ready")).Should().Be(Expect(healthy: true),
            "a check tagged live only must not drag readiness down with it");
    }

    [Fact]
    public async Task The_readiness_probe_is_closed_while_the_startup_seed_is_pending()
    {
        var host = ProbeHost();
        var client = host.CreateClient();
        var gate = host.Services.GetRequiredService<StartupSeedGate>();

        gate.State.Should().Be(StartupSeedState.Completed,
            "the fixture host sets SKIP_SEEDER, so nothing declared a seed");

        try
        {
            gate.MarkPending();

            (await Get(client, "/health/ready")).Should().Be(Expect(healthy: false),
                "no traffic may reach a node whose roles and initial admin do not exist yet");
            (await Get(client, "/health/live")).Should().Be(Expect(healthy: true),
                "a slow seed must not get the pod killed while it runs");

            gate.MarkCompleted();

            (await Get(client, "/health/ready")).Should().Be(Expect(healthy: true));
        }
        finally
        {
            gate.MarkCompleted();
        }
    }

    [Fact]
    public void The_database_check_is_tagged_for_readiness_and_not_for_liveness()
    {
        var registrations = _fixture.Services
            .GetRequiredService<IOptions<HealthCheckServiceOptions>>()
            .Value.Registrations;

        var database = registrations.Single(r => r.Name == "Database");

        database.Tags.Should().Contain("ready");
        database.Tags.Should().NotContain("live",
            "one Postgres restart would otherwise restart every replica at once");

        registrations.Should().Contain(r => r.Tags.Contains("live"),
            "a liveness endpoint that runs no checks cannot report a wedged process");
    }

    private static HttpStatusCode Expect(bool healthy) =>
        healthy ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable;
}
