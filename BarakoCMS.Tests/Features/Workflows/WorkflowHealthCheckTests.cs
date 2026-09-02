using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// A stopped workflow projection is visible without reading the logs.
/// </summary>
/// <remarks>
/// Database, disk and memory all stay green while the projection shard is stopped and every workflow
/// has silently stopped firing, which is the state issue #285 is about.
///
/// This asserts the check is registered and reporting a real number. A health rule nobody wired up
/// still passes all of its own unit tests, and a check that cannot find the shard reports "no
/// progress" forever, which is indistinguishable from the fault it is supposed to detect. Content is
/// created first so there is something for the projection to be caught up on.
/// </remarks>
[Collection("Sequential")]
public class WorkflowHealthCheckTests
{
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(30);

    private readonly IntegrationTestFixture _factory;
    private readonly HttpClient _client;

    public WorkflowHealthCheckTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task The_health_report_names_the_workflow_projection_and_its_lag()
    {
        var (token, _) = await TestHelpers.CreateAdminUserAsync(_factory);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var created = await _client.PostAsJsonAsync("/api/contents", new
        {
            ContentType = $"health-{Guid.NewGuid():N}",
            Data = new Dictionary<string, object> { { "Title", "gives the projection something to do" } },
        });
        created.EnsureSuccessStatusCode();

        var deadline = DateTime.UtcNow + PollTimeout;
        JsonElement entry = default;
        long lag = -1;

        while (DateTime.UtcNow < deadline)
        {
            var response = await _client.GetAsync("/api/monitoring/health");
            response.EnsureSuccessStatusCode();

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            document.RootElement.GetProperty("entries")
                .TryGetProperty("Workflow Projection", out entry)
                .Should().BeTrue(
                    "without this check a stopped projection is invisible to /health, invisible to "
                  + "/metrics, and silent in the logs after the first exception");

            entry = entry.Clone();
            entry.GetProperty("status").GetString().Should().NotBe("Unhealthy",
                "the liveness probe reads /health, and restarting the pod does not restart a stopped "
              + "shard: it resumes at the same event and fails on the same one");

            lag = entry.GetProperty("data").GetProperty("lagEvents").GetInt64();
            if (lag >= 0)
            {
                break;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        lag.Should().BeGreaterThanOrEqualTo(0,
            $"the check never found the '{barakoCMS.Infrastructure.Health.WorkflowProjectionHealthCheck.ProjectionName}' "
          + "shard in Marten's projection progress. It would then report a stalled projection forever, "
          + "which is the fault it exists to detect");
    }
}
