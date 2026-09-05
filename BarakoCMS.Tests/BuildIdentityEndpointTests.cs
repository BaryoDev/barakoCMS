using System.Net;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// /health/build over the real pipeline. The release deploys the playground and then asks this
/// endpoint which commit answered, so it only publishes a build it has seen run. What that depends
/// on is pinned here: the endpoint reports the commit the image was stamped with, an unstamped
/// image says so rather than returning something a comparison might accept, and /health keeps the
/// body it always had.
/// </summary>
[Collection("Sequential")]
public class BuildIdentityEndpointTests
{
    private const string Variable = "BARAKO_BUILD_SHA";

    private readonly IntegrationTestFixture _factory;

    public BuildIdentityEndpointTests(IntegrationTestFixture factory) => _factory = factory;

    // The variable is process-wide, which is why this class is in the Sequential collection and why
    // every test restores what it found.
    private async Task<string> ShaReportedWhenStampedWith(string? value)
    {
        var previous = Environment.GetEnvironmentVariable(Variable);
        Environment.SetEnvironmentVariable(Variable, value);
        try
        {
            using var client = _factory
                .WithWebHostBuilder(_ => { })
                .CreateClient();

            var res = await client.GetAsync("/health/build", TestContext.Current.CancellationToken);
            res.StatusCode.Should().Be(HttpStatusCode.OK);

            var body = await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            return JsonDocument.Parse(body).RootElement.GetProperty("sha").GetString()!;
        }
        finally
        {
            Environment.SetEnvironmentVariable(Variable, previous);
        }
    }

    [Fact]
    public async Task The_endpoint_reports_the_commit_the_build_was_stamped_with()
    {
        var sha = await ShaReportedWhenStampedWith("f0edb95c7b6d9dd79e799c85bed41c081d0ce2a8");

        sha.Should().Be("f0edb95c7b6d9dd79e799c85bed41c081d0ce2a8");
    }

    // An unstamped image must not report something a comparison could accidentally accept. It says
    // unknown, the deploy check compares that to the commit being released, and the release stops.
    [Fact]
    public async Task An_unstamped_build_reports_unknown_rather_than_an_empty_string()
    {
        var sha = await ShaReportedWhenStampedWith(null);

        sha.Should().Be("unknown");
    }

    // The probe contract is unchanged. The build identity is its own path rather than a new field
    // on /health, so a dashboard or a kubelet parsing that body sees exactly what it saw before.
    [Fact]
    public async Task The_health_report_body_is_unchanged()
    {
        using var client = _factory.CreateClient();

        // The core host seeds on a background task and the "seed" check holds readiness closed
        // until it finishes, so /health answers Unhealthy for as long as seeding takes. On a loaded
        // CI runner with fifteen modules that window reached this test twice on Sept 4. Wait for
        // readiness first; the assertion is about the body's shape, not about when seeding ends.
        // Three minutes, not one. Readiness covers the database, disk, memory and the startup seed,
        // and on a runner already hosting the rest of this suite the seed alone has taken past a
        // minute. A minute failed one run and passed the next on the same commit.
        var deadline = DateTime.UtcNow.AddMinutes(3);
        var becameReady = false;
        while (DateTime.UtcNow < deadline)
        {
            var ready = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);
            if (ready.StatusCode == HttpStatusCode.OK) { becameReady = true; break; }
            await Task.Delay(250, TestContext.Current.CancellationToken);
        }

        // Say what actually went wrong, and which check was still holding readiness closed. Without
        // this the test fails on the body assertion and reports a shape mismatch when the real
        // fault was that seeding never finished.
        if (!becameReady)
        {
            var report = await client.GetAsync("/health", TestContext.Current.CancellationToken);
            var reportBody = await report.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            becameReady.Should().BeTrue(
                $"/health/ready did not report OK within three minutes; /health said {report.StatusCode} {reportBody}");
        }

        var res = await client.GetAsync("/health", TestContext.Current.CancellationToken);
        var body = await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        body.Should().Be("{\"status\":\"Healthy\"}");
    }
}
