using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using barakoCMS.Infrastructure.Tracing;
using barakoCMS.Models;
using FluentAssertions;

namespace BarakoCMS.Tests;

/// <summary>
/// Issue #691: the span of a workflow action. The runner has no request, so the span is parented
/// from what the run stored, and it carries ids and names and nothing an action was given.
/// </summary>
/// <remarks>
/// The spans come from the <c>BarakoCMS</c> source through a listener that hears that one source
/// and is disposed with the test. A runner in another test may start a span of its own while one
/// of these runs, so every assertion is on the span this test started.
/// </remarks>
public class WorkflowActionSpanTests
{
    [Fact]
    public void A_workflow_action_span_sits_under_the_request_that_caused_its_run()
    {
        var traceId = ActivityTraceId.CreateRandom();
        var requestSpan = ActivitySpanId.CreateRandom();
        var run = NewRun();
        run.CorrelationId = traceId.ToHexString();
        run.TraceParent = $"00-{traceId.ToHexString()}-{requestSpan.ToHexString()}-01";

        using var listener = ListenToBarakoSpans();
        using var span = BarakoTracing.StartWorkflowAction(run, run.Actions[0], "acme");

        span.Should().NotBeNull("a listener is attached to the source");
        span!.OperationName.Should().Be("workflow.action");
        span.TraceId.Should().Be(traceId, "the action continues the trace of the request, however much later it runs");
        span.ParentSpanId.Should().Be(requestSpan);
    }

    [Fact]
    public void A_workflow_action_span_carries_ids_and_names_and_nothing_from_the_action()
    {
        var run = NewRun();
        run.CorrelationId = "order-17";
        run.Actions[0].Parameters["Url"] = "https://hooks.example.com/services/the-secret-part";
        run.Actions[0].Parameters["Authorization"] = "Bearer a-token";
        run.Actions[0].Error = "401 from the provider, quoting Bearer a-token";

        using var listener = ListenToBarakoSpans();
        using var span = BarakoTracing.StartWorkflowAction(run, run.Actions[0], "acme");
        BarakoTracing.RecordOutcome(span, AttemptStatus.Failed);

        span.Should().NotBeNull();
        var tags = span!.TagObjects.ToDictionary(tag => tag.Key, tag => tag.Value);

        tags.Should().HaveCount(9);
        tags.Should().BeEquivalentTo(new Dictionary<string, object?>
        {
            ["barako.tenant"] = "acme",
            ["barako.workflow.id"] = run.WorkflowDefinitionId.ToString(),
            ["barako.workflow.run_id"] = run.Id.ToString(),
            ["barako.workflow.trigger"] = "Published",
            ["barako.workflow.action"] = "Webhook",
            ["barako.workflow.action_ordinal"] = 0,
            ["barako.workflow.attempt"] = 3,
            ["barako.workflow.outcome"] = "Failed",
            ["barako.correlation_id"] = "order-17",
        });

        span.Status.Should().Be(ActivityStatusCode.Error);
        span.StatusDescription.Should().BeNull("the reason an action failed can quote what was sent to the provider");
    }

    [Fact]
    public void A_run_with_no_stored_origin_starts_a_trace_of_its_own()
    {
        var run = NewRun();
        run.TraceParent = "not a traceparent";
        run.CorrelationId = "not an id\nat all";

        using var listener = ListenToBarakoSpans();
        using var span = BarakoTracing.StartWorkflowAction(run, run.Actions[0], "acme");

        span.Should().NotBeNull();
        span!.ParentSpanId.Should().Be(default(ActivitySpanId), "nothing parseable was stored, so nothing is its parent");
        span.GetTagItem(BarakoTracing.CorrelationIdTag).Should().BeNull(
            "a stored value that is not an id is not put on a span either");
    }

    /// <remarks>
    /// A run whose JSON was edited by hand, or damaged. Throwing here would be before the attempt
    /// runs and on every claim after it, so that run would never finish while tracing is on.
    /// </remarks>
    [Fact]
    public void A_stored_run_with_a_null_trigger_or_action_type_still_gets_its_span()
    {
        var run = NewRun();
        run.TriggerEvent = null!;
        run.Actions[0].ActionType = null!;

        using var listener = ListenToBarakoSpans();
        using var span = BarakoTracing.StartWorkflowAction(run, run.Actions[0], "acme");

        span.Should().NotBeNull();
        span!.GetTagItem(BarakoTracing.WorkflowTriggerTag).Should().Be(string.Empty);
        span.GetTagItem(BarakoTracing.WorkflowActionTag).Should().Be(string.Empty);
    }

    /// <remarks>
    /// What makes "the receiver is sent the same trace" true. The action's span is current while
    /// the action runs, and <c>HttpClient</c> writes <c>traceparent</c> from whatever span is
    /// current. The receiver is a loopback socket that keeps the request it was sent, because a
    /// stubbed handler would sit above the part of the client that writes the header.
    /// </remarks>
    [Fact]
    public async Task An_outbound_call_made_while_the_action_runs_carries_the_trace_to_the_receiver()
    {
        var ct = TestContext.Current.CancellationToken;
        var traceId = ActivityTraceId.CreateRandom();
        var run = NewRun();
        run.TraceParent = $"00-{traceId.ToHexString()}-{ActivitySpanId.CreateRandom().ToHexString()}-01";

        var receiver = new TcpListener(IPAddress.Loopback, 0);
        receiver.Start();
        try
        {
            var received = ReceiveOneRequestAsync(receiver, ct);

            using var listener = ListenToBarakoSpans();
            using var span = BarakoTracing.StartWorkflowAction(run, run.Actions[0], "acme");
            span.Should().NotBeNull();
            Activity.Current.Should().BeSameAs(span, "the span is current for as long as the action runs");

            using var handler = new SocketsHttpHandler { UseProxy = false };
            using var http = new HttpClient(handler);
            var port = ((IPEndPoint)receiver.LocalEndpoint).Port;
            using var response = await http.PostAsync($"http://127.0.0.1:{port}/hook", new StringContent("{}"), ct);

            var request = await received;
            request.Should().StartWith("POST /hook ", "the receiver read the request this test sent");
            request.Should().Contain($"traceparent: 00-{traceId.ToHexString()}-",
                "a webhook receiver that traces can put this call under the request that caused the run");
        }
        finally
        {
            receiver.Stop();
        }
    }

    /// <summary>Takes one connection, reads the request head, answers 204 and returns what it read.</summary>
    private static async Task<string> ReceiveOneRequestAsync(TcpListener receiver, CancellationToken ct)
    {
        using var client = await receiver.AcceptTcpClientAsync(ct);
        await using var stream = client.GetStream();

        var head = new StringBuilder();
        var buffer = new byte[4096];
        while (!head.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            var read = await stream.ReadAsync(buffer, ct);
            if (read == 0) break;
            head.Append(Encoding.ASCII.GetString(buffer, 0, read));
        }

        await stream.WriteAsync(
            Encoding.ASCII.GetBytes("HTTP/1.1 204 No Content\r\nConnection: close\r\n\r\n"), ct);
        return head.ToString();
    }

    private static WorkflowRun NewRun() => new()
    {
        Id = Guid.NewGuid(),
        WorkflowDefinitionId = Guid.NewGuid(),
        WorkflowName = "a name an administrator typed, which is not exported",
        TriggerEvent = "Published",
        Actions =
        {
            new WorkflowActionAttempt { Ordinal = 0, ActionType = "Webhook", Attempts = 2 },
        },
    };

    /// <summary>Hears the <c>BarakoCMS</c> source only, records everything, and stops when disposed.</summary>
    private static ActivityListener ListenToBarakoSpans()
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == BarakoTracing.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}
