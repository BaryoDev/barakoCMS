using System.Net;
using System.Text.Json;
using barakoCMS.Models;
using BarakoCMS.Tests.Features.Workflows;
using FluentAssertions;

namespace BarakoCMS.Tests.Features.WorkflowRuns;

/// <summary>GET /api/workflow-runs takes one status or a comma list, and the counts and ETag follow.</summary>
[Collection("Integration1")]
public class WorkflowRunStatusFilterTests
{
    private const string Message = "Status must be one of: Pending, Running, Succeeded, Failed, PartiallyFailed, Cancelled.";

    private readonly WorkflowStopHarness _harness;

    public WorkflowRunStatusFilterTests(IntegrationTestFixture factory) => _harness = new WorkflowStopHarness(factory);

    private static CancellationToken Ct => WorkflowStopHarness.Ct;

    /// <summary>One entry with a Pending, a Running and a Succeeded run.</summary>
    private async Task<(Guid Content, Guid Pending, Guid Running, Guid Succeeded)> SeedAsync()
    {
        var workflow = Guid.NewGuid();
        var content = await _harness.StoreContentAsync();
        var pending = await _harness.SeedRunAsync(workflow, content, [WorkflowStopHarness.Waiting()]);
        var running = await _harness.SeedRunAsync(workflow, content, [WorkflowStopHarness.RunningElsewhere()]);
        var succeeded = await _harness.SeedRunAsync(workflow, content, [WorkflowStopHarness.Finished(AttemptStatus.Succeeded)]);

        pending.Status.Should().Be(RunStatus.Pending);
        running.Status.Should().Be(RunStatus.Running);
        succeeded.Status.Should().Be(RunStatus.Succeeded);
        return (content, pending.Id, running.Id, succeeded.Id);
    }

    private async Task<(HttpResponseMessage Response, string Body)> GetAsync(string query, string? ifNoneMatch = null)
    {
        var admin = await _harness.AdminAsync();
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/workflow-runs?{query}");
        if (ifNoneMatch is not null) request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        var res = await admin.SendAsync(request, Ct);
        return (res, await res.Content.ReadAsStringAsync(Ct));
    }

    private static List<Guid> Ids(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList();
    }

    [Fact]
    public async Task One_status_lists_only_its_runs()
    {
        var seeded = await SeedAsync();

        var (res, body) = await GetAsync($"contentId={seeded.Content}&status=running");

        res.StatusCode.Should().Be(HttpStatusCode.OK, body);
        Ids(body).Should().Equal(seeded.Running);
    }

    [Fact]
    public async Task Two_statuses_list_the_runs_of_both()
    {
        var seeded = await SeedAsync();

        var (res, body) = await GetAsync($"contentId={seeded.Content}&status=Running,pending");

        res.StatusCode.Should().Be(HttpStatusCode.OK, body);
        Ids(body).Should().BeEquivalentTo([seeded.Running, seeded.Pending]);
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("totalItems").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task A_status_named_twice_or_with_spaces_lists_each_run_once()
    {
        var seeded = await SeedAsync();

        var (res, body) = await GetAsync($"contentId={seeded.Content}&status=Running,%20Pending,Running");

        res.StatusCode.Should().Be(HttpStatusCode.OK, body);
        Ids(body).Should().BeEquivalentTo([seeded.Running, seeded.Pending]);
    }

    [Theory]
    [InlineData("Running,Nope")]
    [InlineData("Nope")]
    [InlineData("Running,,Pending")]
    [InlineData("Running,")]
    public async Task A_name_that_is_not_a_status_is_a_400_with_the_usual_message(string status)
    {
        var (res, body) = await GetAsync($"status={Uri.EscapeDataString(status)}");

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain(Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("%20")]
    public async Task An_empty_value_lists_every_status_as_before(string status)
    {
        var seeded = await SeedAsync();

        var (res, body) = await GetAsync($"contentId={seeded.Content}&status={status}");

        res.StatusCode.Should().Be(HttpStatusCode.OK, body);
        Ids(body).Should().BeEquivalentTo([seeded.Pending, seeded.Running, seeded.Succeeded]);
    }

    [Fact]
    public async Task Counts_do_not_follow_a_multi_status_filter()
    {
        var seeded = await SeedAsync();

        var (res, body) = await GetAsync($"contentId={seeded.Content}&status=Running,Pending");

        res.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var doc = JsonDocument.Parse(body);
        var counts = doc.RootElement.GetProperty("statusCounts");
        counts.GetProperty("Pending").GetInt32().Should().Be(1);
        counts.GetProperty("Running").GetInt32().Should().Be(1);
        counts.GetProperty("Succeeded").GetInt32().Should().Be(1, "a run outside the filter is still counted");
        counts.GetProperty("Failed").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task The_etag_answers_304_for_the_same_multi_status_filter_and_differs_for_another()
    {
        var seeded = await SeedAsync();
        var query = $"contentId={seeded.Content}&status=Running,Pending";

        var (first, _) = await GetAsync(query);
        var etag = first.Headers.ETag!.ToString();

        var (again, againBody) = await GetAsync(query, etag);
        again.StatusCode.Should().Be(HttpStatusCode.NotModified);
        againBody.Should().BeEmpty();

        var (other, _) = await GetAsync($"contentId={seeded.Content}&status=Running,Succeeded");
        other.Headers.ETag!.ToString().Should().NotBe(etag, "a different set of runs is a different page");
    }

    [Fact]
    public async Task The_etag_changes_when_a_run_in_the_filter_arrives()
    {
        var seeded = await SeedAsync();
        var query = $"contentId={seeded.Content}&status=Running,Pending";
        var (first, _) = await GetAsync(query);
        var etag = first.Headers.ETag!.ToString();

        await _harness.SeedRunAsync(Guid.NewGuid(), seeded.Content, [WorkflowStopHarness.Waiting()]);

        var (second, body) = await GetAsync(query, etag);
        second.StatusCode.Should().Be(HttpStatusCode.OK, "the page and the counts changed");
        Ids(body).Should().HaveCount(3);
    }
}
