using System.Net;
using barakoCMS.Core.Interfaces;
using barakoCMS.Features.Workflows;
using barakoCMS.Features.Workflows.Actions;
using barakoCMS.Features.Workflows.Outbox;
using barakoCMS.Infrastructure.Connectors;
using barakoCMS.Infrastructure.Http;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using BarakoCMS.Tests.Features.Workflows;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static BarakoCMS.Tests.Features.Connectors.GrantKit;

namespace BarakoCMS.Tests.Features.Connectors;

/// <summary>
/// Every place a field value can leave the system, one test each: a grant opens the connector paths
/// (Request, dry run, sync, query rows) and nothing else. The webhook, Publish and the outbox still
/// send Public fields only, and Email and SMS still send what their template resolves to.
/// </summary>
[Collection("Integration1")]
public class ConnectorEgressTableTests(IntegrationTestFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_request_step_sends_a_granted_field()
    {
        var subject = await SeedAsync(fixture, ["Ssn"]);
        var sender = new GrantCountingSender();

        var result = await RunStepAsync(fixture, subject, Definition(subject, "{\"ssn\":\"{{Ssn}}\"}"), sender);

        result.Succeeded.Should().BeTrue(result.Error);
        sender.Sent.Should().Be(1);
        sender.Last!.Body.Should().Contain(SsnValue);
    }

    [Fact]
    public async Task A_dry_run_shows_a_granted_field_only_to_a_caller_who_may_read_it()
    {
        var subject = await SeedAsync(fixture, ["Ssn"]);
        var definition = Definition(subject, "{\"ssn\":\"{{Ssn}}\"}");
        await StoreAsync(fixture, definition);
        var reader = await CallerAsync(
            fixture, [SystemCapabilities.ManageRequests, SystemCapabilities.ViewSensitive], subject.Type);
        var blind = await CallerAsync(fixture, [SystemCapabilities.ManageRequests], subject.Type);

        var seen = await reader.PostAsync($"/api/requests/{definition.Slug}/dry-run/{subject.EntryId}", null, Ct);
        var masked = await blind.PostAsync($"/api/requests/{definition.Slug}/dry-run/{subject.EntryId}", null, Ct);

        (await seen.Content.ReadAsStringAsync(Ct)).Should().Contain(SsnValue);
        (await masked.Content.ReadAsStringAsync(Ct)).Should().NotContain(SsnValue);
    }

    [Fact]
    public async Task A_collection_sync_composes_a_request_that_names_a_granted_field_without_refusing_it()
    {
        var subject = await SeedAsync(fixture, ["Ssn"]);
        var fetcher = new GrantCountingFetcher();

        var outcome = await SyncAsync(fixture, subject, Definition(subject, null, "/rows/{{Ssn}}"), fetcher);

        fetcher.Fetched.Should().Be(1, "the grant lets the sync's request through, got: {0}", outcome.Error);
    }

    [Fact]
    public async Task Query_rows_carry_a_granted_field()
    {
        var s = await SeedLinesAsync(fixture, (lines, _) => [lines + ".Note"]);
        var query = new QueryDefinition
        {
            Id = Guid.NewGuid(), Slug = Name("egq"), Name = "Egress", ContentType = s.Lines, Fields = ["Qty", "Note"],
        };
        await StoreAsync(fixture, query);
        var definition = RequestKit.Definition(s.Subject, "{{query.rows}}");
        definition.QuerySlug = query.Slug;

        var composed = await RequestKit.ComposeAsync(fixture, s.Subject, definition);

        composed.Ok.Should().BeTrue("got: {0}", composed.Refusal);
        composed.Body.Should().Contain(NoteValue);
    }

    [Fact]
    public async Task A_webhook_still_sends_public_fields_only_when_the_type_is_granted_to_a_connector()
    {
        var subject = await SeedAsync(fixture, ["Ssn", "Perk"]);
        var entry = await EntryAsync(fixture, subject);
        using var listener = new RecordingListener();
        var guard = new OutboundAddressGuard(isBlocked: _ => false);

        using var scope = fixture.Services.CreateScope();
        using var handler = OutboundHttpHandler.Create(guard);
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        var action = new WebhookAction(
            new OneClient(client),
            scope.ServiceProvider.GetRequiredService<IDocumentSession>(),
            new Moq.Mock<barakoCMS.Infrastructure.Security.ISecretProtector>().Object,
            guard,
            NullLogger<WebhookAction>.Instance);

        await action.ExecuteAsync(new Dictionary<string, string> { ["Url"] = listener.Url }, entry, Ct);

        listener.WasCalled.Should().BeTrue();
        listener.LastBody.Should().Contain("plain title");
        listener.LastBody.Should().NotContain(SsnValue).And.NotContain(PerkValue);
    }

    [Fact]
    public async Task A_publish_payload_naming_a_granted_field_is_still_refused_when_the_workflow_is_saved()
    {
        var subject = await SeedAsync(fixture, ["Ssn"]);
        using var scope = fixture.Services.CreateScope();
        var validator = scope.ServiceProvider.GetRequiredService<IWorkflowSchemaValidator>();

        var refused = await validator.ValidateAsync(PublishWorkflow(subject.Type, "{\"x\":\"{{data.Ssn}}\"}"), Ct);

        refused.IsValid.Should().BeFalse();
        refused.Errors.Should().Contain(e => e.Message.Contains("Ssn"));
    }

    [Fact]
    public async Task A_publish_step_naming_a_granted_field_stages_no_outbox_message()
    {
        var subject = await SeedAsync(fixture, ["Ssn"]);
        var entry = await EntryAsync(fixture, subject);
        using var scope = fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var key = Guid.NewGuid().ToString();
        var action = new PublishAction(session, new OutboxStaging(), NullLogger<PublishAction>.Instance);

        var result = await action.RunAsync(new Dictionary<string, string>
        {
            ["Url"] = "https://example.com/in",
            ["Topic"] = "people",
            ["MessageType"] = "person.updated",
            ["Payload"] = "{\"x\":\"{{data.Ssn}}\"}",
            ["IdempotencyKey"] = key,
        }, entry, Ct);

        result.Succeeded.Should().BeFalse();
        (await session.Query<OutboxMessage>().Where(m => m.ContentId == entry.Id).AnyAsync(Ct)).Should().BeFalse();
    }

    [Fact]
    public async Task An_email_body_is_sent_as_resolved_with_or_without_a_grant_on_the_type()
    {
        var subject = await SeedAsync(fixture, []);
        var entry = await EntryAsync(fixture, subject);
        var parameters = new Dictionary<string, string>
        {
            ["To"] = "someone@example.com", ["Subject"] = "Hello", ["Body"] = "Ref " + SsnValue,
        };

        var without = new RecordingEmail();
        await new EmailAction(without, NullLogger<EmailAction>.Instance).RunAsync(parameters, entry, Ct);
        await RequestKit.SetGrantsAsync(fixture, subject, [subject.Type + ".Ssn"]);
        var granted = new RecordingEmail();
        await new EmailAction(granted, NullLogger<EmailAction>.Instance).RunAsync(parameters, entry, Ct);

        without.Bodies.Should().Equal("Ref " + SsnValue);
        granted.Bodies.Should().Equal(without.Bodies);
    }

    [Fact]
    public async Task An_sms_message_is_sent_as_resolved_with_or_without_a_grant_on_the_type()
    {
        var subject = await SeedAsync(fixture, []);
        var entry = await EntryAsync(fixture, subject);
        var parameters = new Dictionary<string, string> { ["To"] = "+15550100", ["Message"] = "Ref " + SsnValue };

        var without = new RecordingSms();
        await new SmsAction(without, NullLogger<SmsAction>.Instance).RunAsync(parameters, entry, Ct);
        await RequestKit.SetGrantsAsync(fixture, subject, [subject.Type + ".Ssn"]);
        var granted = new RecordingSms();
        await new SmsAction(granted, NullLogger<SmsAction>.Instance).RunAsync(parameters, entry, Ct);

        without.Messages.Should().Equal("Ref " + SsnValue);
        granted.Messages.Should().Equal(without.Messages);
    }

    private static WorkflowDefinition PublishWorkflow(string type, string payload) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Egress " + Guid.NewGuid().ToString("N")[..6],
        TriggerContentType = type,
        TriggerEvent = "Published",
        Actions =
        [
            new WorkflowAction
            {
                Type = "Publish",
                Parameters = new Dictionary<string, string>
                {
                    ["Url"] = "https://example.com/in",
                    ["Topic"] = "people",
                    ["MessageType"] = "person.updated",
                    ["Payload"] = payload,
                    ["IdempotencyKey"] = Guid.NewGuid().ToString(),
                },
            },
        ],
    };

    private sealed class OneClient(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class RecordingEmail : IEmailService
    {
        public List<string> Bodies { get; } = [];

        public Task SendEmailAsync(string to, string subject, string body, CancellationToken cancellationToken = default)
        {
            Bodies.Add(body);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingSms : ISmsService
    {
        public List<string> Messages { get; } = [];

        public Task SendSmsAsync(string to, string message, CancellationToken cancellationToken = default)
        {
            Messages.Add(message);
            return Task.CompletedTask;
        }
    }
}
