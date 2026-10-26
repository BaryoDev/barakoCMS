using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using barakoCMS.Features.Workflows;
using barakoCMS.Infrastructure.Realtime;
using barakoCMS.Models;
using Microsoft.Extensions.DependencyInjection;
using BarakoCMS.Tests.SampleProbe;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Xunit;

namespace BarakoCMS.Tests.Realtime;

/// <summary>
/// The workflow-runs hub topic (a notice per run written, read back through changedSince) and the
/// weak ETag on the run list.
/// </summary>
[Collection("Integration1")]
public class WorkflowRunLiveTests(SampleProbeHost host) : IClassFixture<SampleProbeHost>
{
    private static readonly string[] ViewRuns = ["view_workflow_runs"];
    private static readonly TimeSpan Arrives = TimeSpan.FromSeconds(15);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Listening(HubConnection connection) : IAsyncDisposable
    {
        private readonly Channel<JsonElement> _notices = Channel.CreateUnbounded<JsonElement>();

        public static async Task<Listening> StartAsync(SampleProbeHost host, string token)
        {
            var connection = await RealtimeHubTests.ConnectAsync(host, token);
            var listening = new Listening(connection);
            connection.On<JsonElement>("changed", n => listening._notices.Writer.TryWrite(n));
            await connection.InvokeAsync("Subscribe", "workflow-runs", Ct);
            return listening;
        }

        /// <summary>Every notice up to and including the first one for <paramref name="runId"/>.</summary>
        public async Task<List<JsonElement>> UntilRunAsync(Guid runId)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            timeout.CancelAfter(Arrives);
            var seen = new List<JsonElement>();
            while (true)
            {
                var notice = await _notices.Reader.ReadAsync(timeout.Token);
                seen.Add(notice);
                if (notice.GetProperty("streamId").GetGuid() == runId) return seen;
            }
        }

        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }

    private async Task<WorkflowRun> StoreRunAsync(Guid workflowId, string? tenantSlug = null)
    {
        var run = new WorkflowRun
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = workflowId,
            WorkflowName = "Live",
            ContentId = Guid.NewGuid(),
            ContentType = "article",
            TriggerEvent = WorkflowEvents.Published,
            TriggeringEventSequence = 1,
        };
        run.Actions.Add(new WorkflowActionAttempt
        {
            Ordinal = 0,
            ActionType = "CountingRunner",
            Status = AttemptStatus.Pending,
            IdempotencyKey = $"{run.Id:N}-0",
        });
        run.Recompute();

        await using var session = host.OpenSession(tenantSlug);
        session.Store(run);
        await session.SaveChangesAsync(Ct);
        return run;
    }

    [Fact]
    public async Task A_caller_without_view_workflow_runs_cannot_subscribe()
    {
        var caller = await host.CallerHoldingAsync(["read_probe_tickets"]);
        await using var connection = await RealtimeHubTests.ConnectAsync(host, caller.Token);

        var act = () => connection.InvokeAsync("Subscribe", "workflow-runs", Ct);

        await act.Should().ThrowAsync<HubException>().WithMessage("*view_workflow_runs*");
    }

    [Fact]
    public async Task A_run_that_is_stored_sends_a_notice_with_ids_only()
    {
        var caller = await host.CallerHoldingAsync(ViewRuns);
        await using var listening = await Listening.StartAsync(host, caller.Token);

        var run = await StoreRunAsync(Guid.NewGuid());

        var seen = await listening.UntilRunAsync(run.Id);
        seen.Should().NotBeEmpty();
        var notice = seen.Last();
        notice.GetProperty("topic").GetString().Should().Be("workflow-runs");
        notice.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("topic", "sequence", "streamId", "type");
        notice.GetRawText().Should().NotContain("Live", "a notice never carries the run");
    }

    [Fact]
    public async Task A_run_updated_later_sends_another_notice_and_changedSince_returns_it()
    {
        var caller = await host.CallerHoldingAsync(ViewRuns);
        var workflowId = Guid.NewGuid();
        var run = await StoreRunAsync(workflowId);
        await using var listening = await Listening.StartAsync(host, caller.Token);

        await using (var session = host.OpenSession())
        {
            var stored = await session.LoadAsync<WorkflowRun>(run.Id, Ct);
            stored!.Actions[0].Status = AttemptStatus.Succeeded;
            stored.Recompute();
            session.Update(stored);
            await session.SaveChangesAsync(Ct);
        }

        var seen = await listening.UntilRunAsync(run.Id);
        seen.Should().NotBeEmpty();

        var res = await caller.Client.GetAsync($"/api/workflow-runs?workflowId={workflowId}&changedSince=2020-01-01T00:00:00Z", Ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(Ct));
        var items = doc.RootElement.GetProperty("items");
        items.GetArrayLength().Should().Be(1);
        items[0].GetProperty("id").GetGuid().Should().Be(run.Id);
        items[0].GetProperty("actions")[0].GetProperty("status").GetString().Should().Be("Succeeded");
    }

    [Fact]
    public async Task A_run_of_another_tenant_sends_no_notice()
    {
        var theirTenant = await host.CreateTenantAsync();
        var caller = await host.CallerHoldingAsync(ViewRuns);
        await using var listening = await Listening.StartAsync(host, caller.Token);

        // Theirs first. Notices on one connection arrive in send order, so by the time ours arrives,
        // theirs would already be here if it had been sent.
        var theirs = await StoreRunAsync(Guid.NewGuid(), theirTenant);
        var mine = await StoreRunAsync(Guid.NewGuid());

        var seen = await listening.UntilRunAsync(mine.Id);
        seen.Should().NotBeEmpty();
        seen.Select(n => n.GetProperty("streamId").GetGuid()).Should().NotContain(theirs.Id);
    }

    [Fact]
    public async Task An_unchanged_run_list_answers_304_to_its_own_etag_and_a_changed_one_does_not()
    {
        var caller = await host.CallerHoldingAsync(ViewRuns);
        var workflowId = Guid.NewGuid();
        var run = await StoreRunAsync(workflowId);
        var url = $"/api/workflow-runs?workflowId={workflowId}";

        var first = await caller.Client.GetAsync(url, Ct);
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var etag = first.Headers.ETag;
        etag.Should().NotBeNull();
        etag!.IsWeak.Should().BeTrue();
        using (var doc = JsonDocument.Parse(await first.Content.ReadAsStringAsync(Ct)))
            doc.RootElement.GetProperty("items").GetArrayLength().Should().Be(1);

        var again = new HttpRequestMessage(HttpMethod.Get, url);
        again.Headers.IfNoneMatch.Add(etag);
        var second = await caller.Client.SendAsync(again, Ct);
        second.StatusCode.Should().Be(HttpStatusCode.NotModified);
        (await second.Content.ReadAsStringAsync(Ct)).Should().BeEmpty();

        await using (var session = host.OpenSession())
        {
            var stored = await session.LoadAsync<WorkflowRun>(run.Id, Ct);
            stored!.Actions[0].Status = AttemptStatus.Succeeded;
            stored.Recompute();
            session.Update(stored);
            await session.SaveChangesAsync(Ct);
        }

        var changed = new HttpRequestMessage(HttpMethod.Get, url);
        changed.Headers.IfNoneMatch.Add(etag);
        var third = await caller.Client.SendAsync(changed, Ct);
        third.StatusCode.Should().Be(HttpStatusCode.OK);
        third.Headers.ETag.Should().NotBe(etag);
    }

    [Fact]
    public async Task A_list_without_if_none_match_is_never_a_304()
    {
        var caller = await host.CallerHoldingAsync(ViewRuns);
        var workflowId = Guid.NewGuid();
        await StoreRunAsync(workflowId);
        var url = $"/api/workflow-runs?workflowId={workflowId}";

        var first = await caller.Client.GetAsync(url, Ct);
        var second = await caller.Client.GetAsync(url, Ct);

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        second.Headers.ETag.Should().Be(first.Headers.ETag, "the same list gives the same tag");
    }

    [Fact]
    public async Task A_stored_type_holding_the_reserved_topic_is_not_read_as_a_content_topic()
    {
        var name = await ContentTopicKit.TypeAsync(host, "workflow-runs");
        var catalog = host.Services.GetRequiredService<ContentTopicCatalog>();

        (await catalog.FindAsync(Tenant.DefaultSlug, "workflow-runs", Ct)).Should().BeNull();
        (await catalog.ForTypeAsync(Tenant.DefaultSlug, name, Ct)).Should().BeNull();
    }

    [Fact]
    public async Task A_commit_of_many_runs_sends_one_refetch_notice()
    {
        var caller = await host.CallerHoldingAsync(ViewRuns);
        await using var listening = await Listening.StartAsync(host, caller.Token);
        var marker = await StoreRunAsync(Guid.NewGuid());
        await listening.UntilRunAsync(marker.Id);

        await using (var session = host.OpenSession())
        {
            for (var i = 0; i < WorkflowRunTopic.MaxNoticesPerCommit + 1; i++)
            {
                session.Store(new WorkflowRun
                {
                    Id = Guid.NewGuid(),
                    WorkflowDefinitionId = Guid.NewGuid(),
                    WorkflowName = "Bulk",
                    ContentId = Guid.NewGuid(),
                    ContentType = "article",
                    TriggerEvent = WorkflowEvents.Published,
                });
            }

            await session.SaveChangesAsync(Ct);
        }

        var next = await listening.UntilRunAsync(Guid.Empty);
        next.Should().NotBeEmpty();
        next.Should().ContainSingle(n => n.GetProperty("type").GetString() == WorkflowRunTopic.RefetchType);
        next.Count.Should().BeLessThan(WorkflowRunTopic.MaxNoticesPerCommit, "the runs were not announced one by one");
    }

    [Fact]
    public async Task The_same_list_in_two_tenants_has_different_etags()
    {
        var first = await host.CreateTenantAsync();
        var second = await host.CreateTenantAsync();
        var workflowId = Guid.NewGuid();
        var url = $"/api/workflow-runs?workflowId={workflowId}";

        var a = await (await host.ClientHoldingAsync(ViewRuns, first)).GetAsync(url, Ct);
        var b = await (await host.ClientHoldingAsync(ViewRuns, second)).GetAsync(url, Ct);

        a.StatusCode.Should().Be(HttpStatusCode.OK);
        b.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var doc = JsonDocument.Parse(await a.Content.ReadAsStringAsync(Ct)))
            doc.RootElement.GetProperty("totalItems").GetInt32().Should().Be(0, "both bodies are the same empty page");
        a.Headers.ETag.Should().NotBeNull();
        b.Headers.ETag.Should().NotBe(a.Headers.ETag);
    }

    [Fact]
    public async Task Both_the_200_and_the_304_say_private_no_cache()
    {
        var caller = await host.CallerHoldingAsync(ViewRuns);
        var workflowId = Guid.NewGuid();
        await StoreRunAsync(workflowId);
        var url = $"/api/workflow-runs?workflowId={workflowId}";

        var ok = await caller.Client.GetAsync(url, Ct);
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.IfNoneMatch.Add(ok.Headers.ETag!);
        var notModified = await caller.Client.SendAsync(request, Ct);

        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        notModified.StatusCode.Should().Be(HttpStatusCode.NotModified);
        foreach (var response in new[] { ok, notModified })
        {
            response.Headers.CacheControl!.Private.Should().BeTrue();
            response.Headers.CacheControl.NoCache.Should().BeTrue();
        }
    }
}
