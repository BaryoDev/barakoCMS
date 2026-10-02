using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using barakoCMS.Events;
using barakoCMS.Features.Workflows;
using barakoCMS.Infrastructure.Tracing;
using barakoCMS.Models;
using FluentAssertions;
using JasperFx.Events;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// Issue #691: a caller's trace reaches what the request stores, and what the request sets off.
/// </summary>
/// <remarks>
/// Tracing is not configured on this host, which is the default, so everything here is what a
/// deployment gets with no exporter: the id on the response, on the event, on the workflow run and
/// on the event the run's action writes.
///
/// Each test sends a trace id of its own and reads back the stream or the run it created by id, so
/// nothing another test stores can satisfy or fail an assertion.
/// </remarks>
[Collection("Sequential")]
public class EventCorrelationTests
{
    private const string CorrelationHeader = "X-Correlation-ID";

    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(30);

    private readonly IntegrationTestFixture _factory;
    private readonly HttpClient _client;

    public EventCorrelationTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task A_traceparent_sent_with_a_request_is_on_the_event_the_request_stores()
    {
        await AuthenticateAsync();
        var trace = NewTrace();

        var (id, response) = await CreateContentAsync(NewTypeName(), trace.Header);

        response.Headers.GetValues(CorrelationHeader).Should().ContainSingle().Which.Should().Be(trace.TraceId,
            "a caller that sent traceparent and no correlation id gets its trace id back as the correlation id");

        var created = await FirstEventAsync(id);
        created.CorrelationId.Should().Be(trace.TraceId, "the stored event carries the id the response did");
        created.CausationId.Should().MatchRegex($"^00-{trace.TraceId}-[0-9a-f]{{16}}-[0-9a-f]{{2}}$",
            "the cause is the span of this request, inside the caller's trace");
        created.CausationId.Should().NotContain(trace.SpanId,
            "the caller's span is the parent of the request span, not the span that wrote the event");
    }

    [Fact]
    public async Task A_callers_own_correlation_id_is_stored_and_the_trace_is_kept_as_the_cause()
    {
        await AuthenticateAsync();
        var trace = NewTrace();
        var mine = $"order-{Guid.NewGuid():N}";

        var (id, response) = await CreateContentAsync(NewTypeName(), trace.Header, correlationId: mine);

        response.Headers.GetValues(CorrelationHeader).Should().ContainSingle().Which.Should().Be(mine);

        var created = await FirstEventAsync(id);
        created.CorrelationId.Should().Be(mine, "a client that correlates by its own id finds it on the event");
        created.CausationId.Should().StartWith($"00-{trace.TraceId}-", "and the trace is not lost for it");
    }

    [Fact]
    public async Task Headers_that_are_not_ids_are_neither_echoed_nor_stored()
    {
        await AuthenticateAsync();
        var hugeId = "forged" + new string('x', 10 * 1024);
        var hugeTrace = "00-forged" + new string('y', 10 * 1024);

        var (id, response) = await CreateContentAsync(NewTypeName(), hugeTrace, correlationId: hugeId);

        var echoed = response.Headers.GetValues(CorrelationHeader).Should().ContainSingle().Which;
        echoed.Should().MatchRegex("^[0-9a-f]{32}$", "a header that is not an id is replaced with one that is");

        var created = await FirstEventAsync(id);
        created.CorrelationId.Should().Be(echoed);
        (created.CausationId ?? string.Empty).Should().NotContain("forged",
            "a traceparent that does not parse is not a cause");
        (created.CausationId ?? string.Empty).Length.Should().BeLessThanOrEqualTo(55);
    }

    /// <remarks>
    /// The shape every event stored before 4.6 has, and every event no request caused: both
    /// columns null. Reading the stream and folding it must not care.
    /// </remarks>
    [Fact]
    public async Task An_event_no_request_caused_has_neither_id_and_still_reads_back()
    {
        var id = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Events.StartStream<barakoCMS.Models.Content>(id, new ContentCreated(
                id, NewTypeName(), new Dictionary<string, object> { ["Title"] = "no request" },
                ContentStatus.Draft, Guid.NewGuid(), null, SensitivityLevel.Public, DateTime.UtcNow));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var created = await FirstEventAsync(id);

        created.Data.Should().BeOfType<ContentCreated>().Which.Data["Title"].ToString().Should().Be("no request");
        created.CorrelationId.Should().BeNull("nothing is invented where no request caused the write");
        created.CausationId.Should().BeNull();
    }

    [Fact]
    public async Task A_workflow_run_and_what_its_action_writes_carry_the_request_that_caused_them()
    {
        await AuthenticateAsync();
        var contentType = NewTypeName();
        var probeType = NewTypeName();
        var workflowId = await CreateWorkflowAsync(contentType, probeType);
        var (id, _) = await CreateContentAsync(contentType, traceParent: null);

        var trace = NewTrace();
        using (var publish = new HttpRequestMessage(HttpMethod.Put, $"/api/contents/{id}/status"))
        {
            publish.Headers.TryAddWithoutValidation("traceparent", trace.Header);
            publish.Content = JsonContent.Create(
                new barakoCMS.Features.Content.ChangeStatus.Request { Id = id, NewStatus = ContentStatus.Published });
            (await _client.SendAsync(publish, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        }

        var run = await WaitForRunAsync(workflowId);

        run.Status.Should().Be(RunStatus.Succeeded);
        run.CorrelationId.Should().Be(trace.TraceId, "the run copies the id from the event that triggered it");
        run.TraceParent.Should().StartWith($"00-{trace.TraceId}-",
            "the runner parents each action's span on this, so it has to be the publishing request's span");

        using var answer = JsonDocument.Parse(await _client.GetStringAsync(
            $"/api/workflow-runs/{run.Id}", TestContext.Current.CancellationToken));
        answer.RootElement.GetProperty("correlationId").GetString().Should().Be(trace.TraceId,
            "an operator reading the run gets the id to search the log and the event store with");

        Guid probeId;
        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
            var probes = await session.Query<barakoCMS.Models.Content>()
                .Where(c => c.ContentType == probeType)
                .ToListAsync(TestContext.Current.CancellationToken);
            probes.Should().ContainSingle("the run succeeded, and its one action creates one entry");
            probeId = probes[0].Id;
        }

        var written = await FirstEventAsync(probeId);
        written.CorrelationId.Should().Be(trace.TraceId,
            "the action ran in the runner, long after the request, and what it wrote still belongs to that request");
        written.CausationId.Should().StartWith($"00-{trace.TraceId}-");
    }

    /// <remarks>
    /// The other place a run is queued. An erasure queues its Deleted runs inside the request, on
    /// the request's session, so the origin is the request's own and not an event's. The workflow
    /// has no actions, so the run is finished the moment it is stored and no runner touches it.
    /// </remarks>
    [Fact]
    public async Task A_run_queued_for_an_erasure_carries_the_request_that_erased()
    {
        var ct = TestContext.Current.CancellationToken;
        var contentType = NewTypeName();
        var contentId = Guid.NewGuid();
        var requestId = $"erase-{Guid.NewGuid():N}";

        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new WorkflowDefinition
            {
                Id = Guid.NewGuid(),
                Name = $"correlation-{Guid.NewGuid():N}",
                TriggerContentType = contentType,
                TriggerEvent = WorkflowEvents.Deleted,
            });
            await session.SaveChangesAsync(ct);
        }

        using (Correlation.Begin(requestId))
        using (var scope = _factory.Services.CreateScope())
        {
            var queue = scope.ServiceProvider.GetRequiredService<IWorkflowRunQueue>();
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

            (await queue.QueueDeletedAsync(contentId, contentType, ct)).Should().Be(1);
            await session.SaveChangesAsync(ct);
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
            var runs = await session.Query<WorkflowRun>().Where(r => r.ContentId == contentId).ToListAsync(ct);

            runs.Should().ContainSingle().Which.CorrelationId.Should().Be(requestId);
        }
    }

    private sealed record SentTrace(string TraceId, string SpanId)
    {
        public string Header => $"00-{TraceId}-{SpanId}-01";
    }

    private static SentTrace NewTrace() =>
        new(ActivityTraceId.CreateRandom().ToHexString(), ActivitySpanId.CreateRandom().ToHexString());

    private static string NewTypeName() => $"corr-{Guid.NewGuid():N}";

    private async Task AuthenticateAsync()
    {
        var (token, _) = await TestHelpers.CreateAdminUserAsync(_factory);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private async Task<(Guid Id, HttpResponseMessage Response)> CreateContentAsync(
        string contentType, string? traceParent, string? correlationId = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/contents");
        if (traceParent is not null) request.Headers.TryAddWithoutValidation("traceparent", traceParent);
        if (correlationId is not null) request.Headers.TryAddWithoutValidation(CorrelationHeader, correlationId);
        request.Content = JsonContent.Create(new
        {
            ContentType = contentType,
            Data = new Dictionary<string, object> { { "Title", "correlation subject" } },
        });

        var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);
        response.IsSuccessStatusCode.Should().BeTrue("got {0}", response.StatusCode);

        var created = await response.Content.ReadFromJsonAsync<barakoCMS.Features.Content.Create.Response>(
            ApiJson.Options, TestContext.Current.CancellationToken);
        return (created!.Id, response);
    }

    /// <summary>The event that started a stream this test created, with its metadata.</summary>
    private async Task<IEvent> FirstEventAsync(Guid streamId)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var stream = await session.Events.FetchStreamAsync(streamId, token: TestContext.Current.CancellationToken);

        stream.Should().NotBeEmpty("the write this test made started the stream");
        return stream[0];
    }

    /// <summary>A workflow on Published for one content type, whose action creates one probe entry.</summary>
    private async Task<Guid> CreateWorkflowAsync(string triggerContentType, string probeContentType)
    {
        var workflow = new WorkflowDefinition
        {
            Id = Guid.NewGuid(),
            Name = $"correlation-{Guid.NewGuid():N}",
            TriggerContentType = triggerContentType,
            TriggerEvent = "Published",
            Actions =
            [
                new WorkflowAction
                {
                    Type = "CreateTask",
                    Parameters = new Dictionary<string, string>
                    {
                        ["ContentType"] = probeContentType,
                        ["Title"] = "probe",
                    },
                },
            ],
        };

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(workflow);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        return workflow.Id;
    }

    /// <summary>The run once the runner has finished with it.</summary>
    private async Task<WorkflowRun> WaitForRunAsync(Guid workflowId)
    {
        var deadline = DateTime.UtcNow + PollTimeout;

        while (DateTime.UtcNow < deadline)
        {
            using (var scope = _factory.Services.CreateScope())
            {
                var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
                var run = await session.Query<WorkflowRun>()
                    .Where(r => r.WorkflowDefinitionId == workflowId)
                    .FirstOrDefaultAsync(TestContext.Current.CancellationToken);

                if (run is not null && run.Status is not (RunStatus.Pending or RunStatus.Running))
                {
                    return run;
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), TestContext.Current.CancellationToken);
        }

        throw new Xunit.Sdk.XunitException(
            $"Timed out after {PollTimeout.TotalSeconds:0}s: workflow {workflowId} recorded no finished run");
    }
}
