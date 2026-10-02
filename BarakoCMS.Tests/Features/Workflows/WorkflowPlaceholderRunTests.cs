using barakoCMS.Events;
using barakoCMS.Features.Workflows;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// The path a deployment sends through: a run queued for a ClockOut transition, picked up by the
/// runner, mailed to whoever created the entry.
/// </summary>
/// <remarks>
/// Each test works in a tenant of its own, so the <c>site</c> entry that sets the time zone is the
/// one stored here. The run is stored directly with the sequence of the transition event, which is
/// what the projection writes, so the test does not wait on the daemon.
///
/// The entry was created at 00:30 UTC and clocked out at 09:00 UTC, which in Manila is 8:30 in the
/// morning and 5:00 in the afternoon. The stored entry says it was last changed three hours after
/// that by somebody else, so a transition read from the entry and not from its event shows.
/// </remarks>
[Collection("Sequential")]
public class WorkflowPlaceholderRunTests
{
    private const string Body =
        "<p>You worked {{duration createdAt transition.at}} today, from "
      + "{{createdAt | date \"h:mm tt\"}} to {{transition.at | date \"h:mm tt\"}}.</p>";

    private const string ExpectedBody = "<p>You worked 8 hours 30 minutes today, from 8:30 AM to 5:00 PM.</p>";

    private static readonly DateTime ClockedInAt = new(2026, 9, 14, 0, 30, 0, DateTimeKind.Utc);
    private static readonly DateTime ClockedOutAt = new(2026, 9, 14, 9, 0, 0, DateTimeKind.Utc);

    private readonly IntegrationTestFixture _factory;
    private readonly User _teacher = NewUser("teacher");
    private readonly User _head = NewUser("head");
    private readonly string _type = $"time{Guid.NewGuid():N}"[..16];

    public WorkflowPlaceholderRunTests(IntegrationTestFixture factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Red without the change: the recipient was left as <c>{{createdBy.email}}</c>, which is not
    /// an address, so the action failed and nothing was sent.
    /// </summary>
    [Fact]
    public async Task A_clock_out_run_mails_the_author_the_hours_worked_in_the_tenants_time_zone()
    {
        var sent = await RunClockOutAsync("Email", new()
        {
            ["To"] = "{{createdBy.email}}",
            ["Subject"] = "{{transition.name}} recorded by {{transition.by.name}}",
            ["Body"] = Body,
        });

        sent.Subject.Should().Be($"ClockOut recorded by {_head.Username}");
        sent.Body.Should().Be(ExpectedBody);
    }

    /// <summary>
    /// Red without the change. The same message from an Email inside a Conditional, built by the
    /// host's own container, so the child resolves with the extractor the runner prepared.
    /// </summary>
    [Fact]
    public async Task A_conditionals_child_in_a_clock_out_run_mails_the_author_in_the_tenants_time_zone()
    {
        var children = System.Text.Json.JsonSerializer.Serialize(new[]
        {
            new { Type = "Email", Parameters = new Dictionary<string, string>
            {
                ["To"] = "{{createdBy.email}}",
                ["Subject"] = "Child of {{transition.name}}",
                ["Body"] = Body,
            } },
        });

        var sent = await RunClockOutAsync("Conditional", new()
        {
            ["Condition"] = "{{contentType}} == " + _type,
            ["ThenActions"] = children,
        });

        sent.Subject.Should().Be("Child of ClockOut");
        sent.Body.Should().Be(ExpectedBody);
    }

    /// <summary>Queues one ClockOut run with one action and returns the one message it sent the author.</summary>
    private async Task<RecordingEmailService.Sent> RunClockOutAsync(string actionType, Dictionary<string, string> parameters)
    {
        var tenant = $"clock-{Guid.NewGuid():N}"[..20];
        var store = _factory.Services.GetRequiredService<IDocumentStore>();
        var contentId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        await using (var session = store.LightweightSession(tenant))
        {
            session.Store(_teacher);
            session.Store(_head);
            session.Store(new Content
            {
                Id = Guid.NewGuid(),
                ContentType = "site",
                Status = ContentStatus.Published,
                Data = new Dictionary<string, object> { ["Name"] = "School", ["TimeZone"] = "Asia/Manila" },
            });

            session.Events.StartStream<Content>(
                contentId,
                new ContentCreated(contentId, _type, new Dictionary<string, object>(), ContentStatus.Draft,
                    _teacher.Id, null, SensitivityLevel.Public, ClockedInAt),
                new ContentTransitioned(contentId, "ClockOut", "In", "Out", _head.Id, ClockedOutAt));

            session.Store(new Content
            {
                Id = contentId,
                ContentType = _type,
                CreatedAt = ClockedInAt,
                UpdatedAt = ClockedOutAt.AddHours(3),
                CreatedBy = _teacher.Id,
                LastModifiedBy = Guid.NewGuid(),
                LifecycleState = "Out",
            });
            await session.SaveChangesAsync(Ct);

            var stream = await session.Events.FetchStreamAsync(contentId, token: Ct);
            stream.Should().HaveCount(2);

            var run = new WorkflowRun
            {
                Id = runId,
                WorkflowDefinitionId = Guid.NewGuid(),
                WorkflowName = "Clock out",
                // The runner looks only at the 20 oldest unfinished runs of a tenant. Oldest means it is seen.
                CreatedAt = DateTimeOffset.UnixEpoch,
                ContentId = contentId,
                ContentType = _type,
                TriggerEvent = WorkflowEvents.ForTransition("ClockOut"),
                TriggeringEventSequence = stream.Single(e => e.Data is ContentTransitioned).Sequence,
                Actions =
                [
                    new WorkflowActionAttempt
                    {
                        Ordinal = 0,
                        ActionType = actionType,
                        IdempotencyKey = $"{Guid.NewGuid():N}",
                        Parameters = parameters,
                    },
                ],
            };
            run.Recompute();
            session.Store(run);
            await session.SaveChangesAsync(Ct);
        }

        // Either this runner or the fixture's hosted one may claim it; both send through the
        // fixture's recording transport, so wait for the message and not for a particular runner.
        var runner = new WorkflowRunner(
            _factory.Services,
            _factory.Services.GetRequiredService<Microsoft.Extensions.Logging.ILogger<WorkflowRunner>>(),
            _factory.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>());

        List<RecordingEmailService.Sent> sent = [];
        for (var i = 0; i < 100 && sent.Count == 0; i++)
        {
            await runner.RunOnceAsync(Ct);
            sent = _factory.Email.Messages.Where(m => m.To == _teacher.Email).ToList();
            if (sent.Count == 0) await Task.Delay(100, Ct);
        }

        sent.Should().HaveCount(1, "the queued run mails the author once; {0}", await RunStateAsync(store, tenant, runId));
        return sent[0];
    }

    private static User NewUser(string name)
    {
        var id = Guid.NewGuid();
        return new User { Id = id, Username = $"{name}-{id:N}", Email = $"{name}-{id:N}@example.com" };
    }

    private static async Task<string> RunStateAsync(IDocumentStore store, string tenant, Guid runId)
    {
        await using var query = store.QuerySession(tenant);
        var run = await query.LoadAsync<WorkflowRun>(runId, Ct);

        return run is null
            ? "the run is gone"
            : $"run {run.Status}; " + string.Join("; ", run.Actions.Select(a =>
                $"action {a.Ordinal} {a.Status}, attempts {a.Attempts}, error '{a.Error}'"));
    }
}
