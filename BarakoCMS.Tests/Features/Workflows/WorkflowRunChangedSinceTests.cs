using System.Globalization;
using System.Net;
using System.Text.Json;
using barakoCMS.Features.Workflows;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// The run list's changedSince cursor, its per-status counts and its retention cutoffs.
/// </summary>
/// <remarks>
/// The stamp is written by the store on every save, so a test that needs a particular UpdatedAt
/// sets it with SQL after seeding, the way an older or slower writer would have left it.
/// </remarks>
[Collection("Integration1")]
public class WorkflowRunChangedSinceTests
{
    private const string Long = "2019-12-31T00:00:00Z";

    private readonly IntegrationTestFixture _factory;
    private readonly WorkflowStopHarness _harness;

    public WorkflowRunChangedSinceTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _harness = new WorkflowStopHarness(factory);
    }

    private static CancellationToken Ct => WorkflowStopHarness.Ct;

    private async Task StampAsync(Guid runId, DateTimeOffset at)
    {
        await using var session = _harness.Store.LightweightSession();
        session.QueueSqlCommand(
            "update public.mt_doc_workflow_runs set data = jsonb_set(data, '{UpdatedAt}', to_jsonb(?::text)) where id = ?",
            at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture), runId);
        await session.SaveChangesAsync(Ct);
    }

    private async Task StampCreatedAsync(Guid runId, DateTimeOffset at)
    {
        await using var session = _harness.Store.LightweightSession();
        session.QueueSqlCommand(
            "update public.mt_doc_workflow_runs set data = jsonb_set(data, '{CreatedAt}', to_jsonb(?::text)) where id = ?",
            at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture), runId);
        await session.SaveChangesAsync(Ct);
    }

    private async Task<(Guid ContentId, List<WorkflowRun> Runs)> SeedAsync(int count)
    {
        var contentId = await _harness.StoreContentAsync();
        var runs = await _harness.SeedRunsAsync(Guid.NewGuid(), contentId, count);
        return (contentId, runs);
    }

    private async Task<HttpClient> AdminOnAsync(FakeTimeProvider clock)
    {
        var host = _factory.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<TimeProvider>(clock)));
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", await _factory.StoredUserTokenAsync("SuperAdmin", "Admin"));
        return client;
    }

    private Task<JsonDocument> ListAsync(string query) => ListAsync(query, null);

    private async Task<JsonDocument> ListAsync(string query, HttpClient? client)
    {
        var admin = client ?? await _harness.AdminAsync();
        var res = await admin.GetAsync($"/api/workflow-runs?{query}", Ct);
        var body = await res.Content.ReadAsStringAsync(Ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, "{0}", body);
        return JsonDocument.Parse(body);
    }

    private static List<Guid> Ids(JsonDocument doc) =>
        doc.RootElement.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList();

    private static string CursorOf(JsonDocument doc) => doc.RootElement.GetProperty("cursor").GetString()!;

    [Fact]
    public async Task Two_runs_sharing_one_stamp_come_back_exactly_once()
    {
        var (contentId, runs) = await SeedAsync(2);
        var at = DateTimeOffset.UtcNow.AddHours(-1);
        foreach (var run in runs) await StampAsync(run.Id, at);

        using var first = await ListAsync($"contentId={contentId}&changedSince={Long}");
        var ids = Ids(first);

        ids.Should().HaveCount(2);
        ids.Should().BeEquivalentTo(runs.Select(r => r.Id));

        using var second = await ListAsync($"contentId={contentId}&changedSince={CursorOf(first)}");

        Ids(second).Should().BeEmpty("both were read, and neither changed since");
    }

    [Fact]
    public async Task More_runs_than_a_page_are_read_by_following_the_cursor_with_none_missed_or_repeated()
    {
        var (contentId, runs) = await SeedAsync(5);
        var start = DateTimeOffset.UtcNow.AddHours(-2);
        // Three share a stamp, so a page boundary falls inside a tie.
        var stamps = new[] { 0, 1, 1, 1, 2 };
        for (var i = 0; i < runs.Count; i++) await StampAsync(runs[i].Id, start.AddMinutes(stamps[i]));

        var seen = new List<Guid>();
        var cursor = Long;
        for (var page = 0; page < 10; page++)
        {
            using var doc = await ListAsync($"contentId={contentId}&changedSince={cursor}&pageSize=2");
            var ids = Ids(doc);
            if (ids.Count == 0) break;

            seen.AddRange(ids);
            cursor = CursorOf(doc);
        }

        seen.Should().HaveCount(5);
        seen.Should().OnlyHaveUniqueItems();
        seen.Should().BeEquivalentTo(runs.Select(r => r.Id));
        seen.First().Should().Be(runs[0].Id, "the oldest change comes first");
        seen.Last().Should().Be(runs[4].Id);
    }

    [Fact]
    public async Task A_run_that_commits_late_with_an_older_stamp_still_appears_on_the_next_poll()
    {
        var (contentId, runs) = await SeedAsync(2);
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var admin = await AdminOnAsync(clock);
        var now = clock.GetUtcNow();
        await StampAsync(runs[0].Id, now.AddSeconds(-1));
        // runs[1] has not committed its stamp yet.
        await using (var session = _harness.Store.LightweightSession())
        {
            session.QueueSqlCommand("update public.mt_doc_workflow_runs set data = data - 'UpdatedAt' where id = ?", runs[1].Id);
            await session.SaveChangesAsync(Ct);
        }

        using var first = await ListAsync($"contentId={contentId}&changedSince={Long}", admin);
        Ids(first).Should().Equal(runs[0].Id);

        await StampAsync(runs[1].Id, now.AddSeconds(-3));

        using var second = await ListAsync($"contentId={contentId}&changedSince={CursorOf(first)}", admin);

        Ids(second).Should().Contain(runs[1].Id, "the cursor lags behind now by a margin for exactly this");
    }

    [Fact]
    public async Task Nothing_changed_returns_an_empty_page_and_the_same_cursor()
    {
        var contentId = await _harness.StoreContentAsync();

        using var first = await ListAsync($"contentId={contentId}&changedSince={Long}");
        Ids(first).Should().BeEmpty();

        using var second = await ListAsync($"contentId={contentId}&changedSince={CursorOf(first)}");

        Ids(second).Should().BeEmpty();
        CursorOf(second).Should().Be(CursorOf(first));
    }

    [Fact]
    public async Task A_changedSince_that_is_not_a_cursor_is_refused()
    {
        var admin = await _harness.AdminAsync();

        var res = await admin.GetAsync("/api/workflow-runs?changedSince=not-a-cursor", Ct);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("3155378976000000000_0123456789abcdef0123456789abcdef", "ticks past the last date")]
    [InlineData("3155378975999999999_0123456789abcdef0123456789abcdef", "ticks at the last date")]
    [InlineData("9999-12-31T23:59:59Z", "a timestamp far ahead")]
    [InlineData("99999999999999999999_0123456789abcdef0123456789abcdef", "ticks past a long")]
    [InlineData("-5_0123456789abcdef0123456789abcdef", "negative ticks")]
    [InlineData("638000000000000000_not-a-guid", "a bad id")]
    [InlineData("1_2_0123456789abcdef0123456789abcdef", "extra underscores")]
    [InlineData("", "an empty value")]
    public async Task A_tampered_cursor_is_refused(string cursor, string why)
    {
        var admin = await _harness.AdminAsync();

        var res = await admin.GetAsync($"/api/workflow-runs?changedSince={Uri.EscapeDataString(cursor)}", Ct);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, why);
    }

    [Fact]
    public async Task A_list_without_changedSince_has_no_cursor_and_stays_newest_first()
    {
        var (contentId, runs) = await SeedAsync(3);
        var now = DateTimeOffset.UtcNow;
        // Neither creation order nor id order, so only a CreatedAt sort gives this answer.
        await StampCreatedAsync(runs[0].Id, now.AddMinutes(-20));
        await StampCreatedAsync(runs[1].Id, now.AddMinutes(-10));
        await StampCreatedAsync(runs[2].Id, now.AddMinutes(-30));

        using var doc = await ListAsync($"contentId={contentId}");

        doc.RootElement.TryGetProperty("cursor", out var cursor).Should().BeTrue();
        cursor.ValueKind.Should().Be(JsonValueKind.Null);
        Ids(doc).Should().Equal(runs[1].Id, runs[0].Id, runs[2].Id);
    }

    [Fact]
    public async Task A_changedSince_poll_leaves_status_counts_null_and_a_plain_list_has_them()
    {
        var (contentId, _) = await SeedAsync(1);

        using var poll = await ListAsync($"contentId={contentId}&changedSince={Long}");
        using var plain = await ListAsync($"contentId={contentId}");

        poll.RootElement.GetProperty("statusCounts").ValueKind.Should().Be(JsonValueKind.Null);
        plain.RootElement.GetProperty("statusCounts").ValueKind.Should().Be(JsonValueKind.Object);
    }

    [Fact]
    public async Task A_full_page_of_runs_changed_moments_ago_gives_a_cursor_that_still_catches_a_late_commit()
    {
        var (contentId, runs) = await SeedAsync(3);
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var admin = await AdminOnAsync(clock);
        var now = clock.GetUtcNow();
        await StampAsync(runs[0].Id, now.AddSeconds(-2));
        await StampAsync(runs[1].Id, now.AddSeconds(-1));
        // runs[2] has not committed its stamp yet, and will when it does be older than runs[1].
        await using (var session = _harness.Store.LightweightSession())
        {
            session.QueueSqlCommand("update public.mt_doc_workflow_runs set data = data - 'UpdatedAt' where id = ?", runs[2].Id);
            await session.SaveChangesAsync(Ct);
        }

        using var first = await ListAsync($"contentId={contentId}&changedSince={Long}&pageSize=1", admin);
        Ids(first).Should().Equal(runs[0].Id);

        await StampAsync(runs[2].Id, now.AddSeconds(-3));

        var seen = new List<Guid>(Ids(first));
        var cursor = CursorOf(first);
        for (var page = 0; page < 10; page++)
        {
            using var doc = await ListAsync($"contentId={contentId}&changedSince={cursor}&pageSize=1", admin);
            var ids = Ids(doc);
            if (ids.Count == 0) break;

            seen.AddRange(ids);
            cursor = CursorOf(doc);
        }

        seen.Should().Contain(runs[2].Id, "a page with more behind it lags too, or this run is skipped for good");
    }

    [Fact]
    public async Task More_runs_changed_moments_ago_than_a_page_holds_are_all_reached_by_following_the_cursor()
    {
        var (contentId, runs) = await SeedAsync(6);
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var admin = await AdminOnAsync(clock);

        var seen = new HashSet<Guid>();
        var cursor = Long;
        for (var turn = 0; turn < 10; turn++)
        {
            using var doc = await ListAsync($"contentId={contentId}&changedSince={Uri.EscapeDataString(cursor)}&pageSize=2", admin);
            var ids = Ids(doc);
            seen.UnionWith(ids);
            cursor = CursorOf(doc);
            if (ids.Count >= doc.RootElement.GetProperty("totalItems").GetInt32()) break;

            // A request takes time, so the margin point moves between pages.
            clock.Advance(TimeSpan.FromMilliseconds(1));
        }

        seen.Should().HaveCount(6, "every run sits inside the margin, and a page with more behind it must still move on");
        seen.Should().BeEquivalentTo(runs.Select(r => r.Id));
    }

    [Fact]
    public async Task Status_counts_cover_every_status_and_ignore_the_status_filter_and_the_page()
    {
        var contentId = await _harness.StoreContentAsync();
        var workflowId = Guid.NewGuid();
        await _harness.SeedRunAsync(workflowId, contentId, [WorkflowStopHarness.Waiting()]);
        await _harness.SeedRunAsync(workflowId, contentId, [WorkflowStopHarness.Waiting()]);
        await _harness.SeedRunAsync(workflowId, contentId, [WorkflowStopHarness.Finished(AttemptStatus.Succeeded)]);
        await _harness.SeedRunAsync(workflowId, contentId, [WorkflowStopHarness.Finished(AttemptStatus.Failed)]);
        await _harness.SeedRunAsync(workflowId, contentId, [WorkflowStopHarness.Finished(AttemptStatus.Failed)]);
        await _harness.SeedRunAsync(workflowId, contentId, [WorkflowStopHarness.Finished(AttemptStatus.Succeeded), WorkflowStopHarness.Finished(AttemptStatus.Failed)]);
        await _harness.SeedRunAsync(workflowId, contentId, [WorkflowStopHarness.RunningElsewhere()]);
        await _harness.SeedRunAsync(workflowId, contentId, [WorkflowStopHarness.Finished(AttemptStatus.Cancelled)]);

        var expected = new Dictionary<string, int>
        {
            ["Pending"] = 2, ["Running"] = 1, ["Succeeded"] = 1, ["Failed"] = 2, ["PartiallyFailed"] = 1, ["Cancelled"] = 1,
        };

        foreach (var query in new[]
        {
            $"contentId={contentId}",
            $"contentId={contentId}&status=Failed",
            $"contentId={contentId}&status=Pending&pageSize=1&page=2",
        })
        {
            using var doc = await ListAsync(query);
            var counts = doc.RootElement.GetProperty("statusCounts").EnumerateObject()
                .ToDictionary(p => p.Name, p => p.Value.GetInt32());

            counts.Should().HaveCount(6, query);
            counts.Should().BeEquivalentTo(expected, query);
        }

        using var page2 = await ListAsync($"contentId={contentId}&status=Pending&pageSize=1&page=2");
        Ids(page2).Should().HaveCount(1);
        page2.RootElement.GetProperty("totalItems").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task The_retention_cutoffs_are_returned_and_follow_the_configured_windows()
    {
        var config = _factory.Services.GetRequiredService<IConfiguration>();
        var windows = WorkflowRunRetentionService.ReadWindows(config);
        var sweeping = WorkflowRunRetentionService.IsEnabled(config);

        using var doc = await ListAsync("pageSize=1");

        void Check(string name, int days)
        {
            var value = doc.RootElement.GetProperty(name);
            if (!sweeping || days <= 0)
            {
                value.ValueKind.Should().Be(JsonValueKind.Null, name);
                return;
            }

            value.GetDateTimeOffset().Should().BeCloseTo(DateTimeOffset.UtcNow.AddDays(-days), TimeSpan.FromMinutes(5), name);
        }

        Check("succeededRetainedFrom", windows.SucceededDays);
        Check("failedRetainedFrom", windows.FailedDays);
    }
}
