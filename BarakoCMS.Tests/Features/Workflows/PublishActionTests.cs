using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using barakoCMS.Features.Workflows.Actions;
using barakoCMS.Features.Workflows.Outbox;
using barakoCMS.Infrastructure.Http;
using barakoCMS.Infrastructure.Security;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// The Publish action: it sends only what its payload names, refuses a field that is not Public when
/// the workflow is saved, signs what it sends, rides out an outage, and leaves no message behind when
/// the attempt is rolled back.
/// </summary>
[Collection("Sequential")]
public class PublishActionTests
{
    private const string Secret = "whsec_publish_test_4d1b";

    private readonly IntegrationTestFixture _fixture;

    public PublishActionTests(IntegrationTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Only_the_fields_the_payload_names_are_sent()
    {
        var tenant = "publish-named";
        var type = await StoreTypeAsync(tenant, ("Title", SensitivityLevel.Public), ("Notes", SensitivityLevel.Public));
        using var listener = new RecordingListener();

        await QueueAsync(tenant, type, listener.Url, """{"title":"{{data.Title}}"}""", secret: null);
        await DeliverAsync(tenant, DateTimeOffset.UtcNow);

        listener.WasCalled.Should().BeTrue();
        using var body = JsonDocument.Parse(listener.LastBody!);
        var payload = body.RootElement.GetProperty("payload");
        var names = payload.EnumerateObject().Select(p => p.Name).ToList();

        names.Should().NotBeEmpty("an assertion over a collection proves nothing on an empty one");
        names.Should().Equal("title");
        payload.GetProperty("title").GetString().Should().Be("hello");
        listener.LastBody.Should().NotContain("private notes", "a field the payload does not name is not sent");
    }

    [Fact]
    public async Task A_payload_naming_a_Sensitive_field_is_refused_when_the_workflow_is_saved()
    {
        var type = "publish-sensitive-" + Guid.NewGuid().ToString("N")[..8];
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(Definition(type, ("Title", SensitivityLevel.Public), ("Salary", SensitivityLevel.Sensitive)));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        var validator = scope.ServiceProvider.GetRequiredService<IWorkflowSchemaValidator>();

        var refused = await validator.ValidateAsync(
            Workflow(type, """{"pay":"{{data.Salary}}"}"""), TestContext.Current.CancellationToken);
        var allowed = await validator.ValidateAsync(
            Workflow(type, """{"title":"{{data.Title}}"}"""), TestContext.Current.CancellationToken);

        refused.IsValid.Should().BeFalse();
        refused.Errors.Should().NotBeEmpty();
        refused.Errors.Should().Contain(e => e.Message.Contains("Salary") && e.Field == "actions[0].parameters.Payload");
        allowed.Errors.Where(e => e.Field.Contains("Payload")).Should().BeEmpty("a Public field is publishable");
    }

    [Fact]
    public async Task A_placeholder_outside_the_allowlist_is_refused_when_the_workflow_is_saved()
    {
        var type = "publish-allowlist-" + Guid.NewGuid().ToString("N")[..8];
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(Definition(type, ("Title", SensitivityLevel.Public)));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        var validator = scope.ServiceProvider.GetRequiredService<IWorkflowSchemaValidator>();

        var result = await validator.ValidateAsync(
            Workflow(type, """{"who":"{{createdBy.email}}"}"""), TestContext.Current.CancellationToken);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Message.Contains("createdBy.email"));
    }

    [Fact]
    public async Task A_payload_that_is_not_a_JSON_object_of_text_is_refused_when_the_workflow_is_saved()
    {
        var type = "publish-shape-" + Guid.NewGuid().ToString("N")[..8];
        using var scope = _fixture.Services.CreateScope();
        var validator = scope.ServiceProvider.GetRequiredService<IWorkflowSchemaValidator>();

        var result = await validator.ValidateAsync(Workflow(type, "[1,2]"), TestContext.Current.CancellationToken);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Field == "actions[0].parameters.Payload");
    }

    [Fact]
    public async Task The_signature_verifies_at_the_subscriber_with_the_documented_recipe()
    {
        var tenant = "publish-signed";
        var type = await StoreTypeAsync(tenant, ("Title", SensitivityLevel.Public));
        using var listener = new RecordingListener();

        await QueueAsync(tenant, type, listener.Url, """{"title":"{{data.Title}}"}""", secret: Secret);
        await DeliverAsync(tenant, DateTimeOffset.UtcNow, allowInsecure: true);

        listener.WasCalled.Should().BeTrue();
        var signature = listener.LastHeaders["X-Barako-Signature"];
        var timestamp = listener.LastHeaders["X-Barako-Timestamp"];

        Recipe(Secret, timestamp, listener.LastBodyBytes!).Should().Be(signature);
        Recipe("another secret", timestamp, listener.LastBodyBytes!).Should().NotBe(signature);

        using var body = JsonDocument.Parse(listener.LastBody!);
        listener.LastHeaders["X-Barako-Delivery"].Should().Be(body.RootElement.GetProperty("messageId").GetString());
        listener.LastHeaders["Idempotency-Key"].Should().Be(listener.LastHeaders["X-Barako-Delivery"]);
    }

    [Fact]
    public async Task An_outage_delays_the_message_and_it_is_delivered_once_the_receiver_is_back()
    {
        var tenant = "publish-outage";
        var type = await StoreTypeAsync(tenant, ("Title", SensitivityLevel.Public));
        using var down = new RecordingListener(statusCode: 503);
        var id = await QueueAsync(tenant, type, down.Url, """{"title":"{{data.Title}}"}""", secret: null);
        var start = DateTimeOffset.UtcNow;

        await DeliverAsync(tenant, start);

        var delayed = await LoadAsync(tenant, id);
        down.WasCalled.Should().BeTrue();
        delayed.Status.Should().Be(OutboxStatus.Pending, "an outage delays a message and does not lose it");
        delayed.Attempts.Should().Be(1);
        delayed.NextAttemptAt.Should().BeAfter(start);

        // Nothing is sent before the wait is over.
        using var up = new RecordingListener();
        await RepointAsync(tenant, id, up.Url);
        await DeliverAsync(tenant, start);
        up.WasCalled.Should().BeFalse("the backoff has not passed");

        await DeliverAsync(tenant, delayed.NextAttemptAt.AddSeconds(1));

        up.WasCalled.Should().BeTrue();
        var delivered = await LoadAsync(tenant, id);
        delivered.Status.Should().Be(OutboxStatus.Delivered);
        delivered.Attempts.Should().Be(2);

        await using var check = _fixture.Services.GetRequiredService<IDocumentStore>().QuerySession(tenant);
        var rows = await check.Query<WebhookDelivery>().ToListAsync(TestContext.Current.CancellationToken);
        rows.Should().HaveCount(2, "each send leaves a delivery row, the failed one and the good one");
        rows.Select(r => r.ResponseStatus).Should().BeEquivalentTo(new int?[] { 503, 200 });
    }

    [Fact]
    public async Task A_receiver_that_refuses_the_message_dead_letters_it()
    {
        var tenant = "publish-refused";
        var type = await StoreTypeAsync(tenant, ("Title", SensitivityLevel.Public));
        using var listener = new RecordingListener(statusCode: 400);
        var id = await QueueAsync(tenant, type, listener.Url, """{"title":"{{data.Title}}"}""", secret: null);

        await DeliverAsync(tenant, DateTimeOffset.UtcNow);

        var message = await LoadAsync(tenant, id);
        message.Status.Should().Be(OutboxStatus.DeadLettered, "sending a message the receiver calls invalid again does not fix it");
        message.Attempts.Should().Be(1);
    }

    [Fact]
    public async Task A_rolled_back_attempt_writes_no_message()
    {
        var tenant = "publish-rollback";
        var type = await StoreTypeAsync(tenant, ("Title", SensitivityLevel.Public));
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        var staging = new OutboxStaging { Holding = true };

        await using (var session = store.LightweightSession(tenant))
        {
            var action = new PublishAction(session, staging, NullLogger<PublishAction>.Instance);
            var result = await action.RunAsync(Parameters("https://example.com/in", """{"title":"{{data.Title}}"}""", null), Entry(type), TestContext.Current.CancellationToken);
            result.Succeeded.Should().BeTrue(result.Error);

            // The runner stores the staged messages in the save that records the attempt. Here the
            // attempt is dropped, so the session is disposed without that save.
        }

        staging.Messages.Should().HaveCount(1, "the message is held for the run's save");

        await using var check = store.QuerySession(tenant);
        (await check.Query<OutboxMessage>().CountAsync(TestContext.Current.CancellationToken))
            .Should().Be(0, "nothing is stored until the run's record is");
    }

    [Fact]
    public async Task A_content_entry_that_is_not_Public_publishes_nothing()
    {
        var tenant = "publish-private-entry";
        var type = await StoreTypeAsync(tenant, ("Title", SensitivityLevel.Public));
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        var staging = new OutboxStaging { Holding = true };
        var entry = Entry(type);
        entry.Sensitivity = SensitivityLevel.Sensitive;

        await using var session = store.LightweightSession(tenant);
        var result = await new PublishAction(session, staging, NullLogger<PublishAction>.Instance)
            .RunAsync(Parameters("https://example.com/in", """{"title":"{{data.Title}}"}""", null), entry, TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Retryable.Should().BeFalse();
        staging.Messages.Should().BeEmpty();
    }

    private async Task<string> StoreTypeAsync(string tenant, params (string Name, SensitivityLevel Level)[] fields)
    {
        var type = $"{tenant}-record";
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession(tenant);
        session.Store(Definition(type, fields));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return type;
    }

    private static ContentTypeDefinition Definition(string name, params (string Name, SensitivityLevel Level)[] fields) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Fields = fields.Select(f => new FieldDefinition { Name = f.Name, Type = "text", Sensitivity = f.Level }).ToList(),
    };

    private static WorkflowDefinition Workflow(string type, string payload) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Publish " + Guid.NewGuid().ToString("N")[..6],
        TriggerContentType = type,
        TriggerEvent = "Published",
        Actions =
        [
            new WorkflowAction { Type = "Publish", Parameters = Parameters("https://example.com/in", payload, null) },
        ],
    };

    private static Dictionary<string, string> Parameters(string url, string payload, string? secretCiphertext)
    {
        var parameters = new Dictionary<string, string>
        {
            ["Url"] = url,
            ["Topic"] = "articles",
            ["MessageType"] = "article.published",
            ["Payload"] = payload,
            ["IdempotencyKey"] = Guid.NewGuid().ToString(),
        };
        if (secretCiphertext is not null) parameters["Secret"] = secretCiphertext;
        return parameters;
    }

    private static Content Entry(string type) => new()
    {
        Id = Guid.NewGuid(),
        ContentType = type,
        Status = ContentStatus.Published,
        Sensitivity = SensitivityLevel.Public,
        Data = new Dictionary<string, object> { ["Title"] = "hello", ["Notes"] = "private notes" },
    };

    /// <summary>Runs the action outside the runner, which stores the message itself, and returns its id.</summary>
    private async Task<Guid> QueueAsync(string tenant, string type, string url, string payload, string? secret)
    {
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        var protector = _fixture.Services.GetRequiredService<ISecretProtector>();

        await using var session = store.LightweightSession(tenant);
        var action = new PublishAction(session, new OutboxStaging(), NullLogger<PublishAction>.Instance);
        var result = await action.RunAsync(
            Parameters(url, payload, secret is null ? null : protector.Protect(secret)), Entry(type), TestContext.Current.CancellationToken);
        result.Succeeded.Should().BeTrue(result.Error);

        var messages = await session.Query<OutboxMessage>().ToListAsync(TestContext.Current.CancellationToken);
        messages.Should().NotBeEmpty("the action stores the message when no runner is holding it");
        return messages.OrderByDescending(m => m.CreatedAt).First().Id;
    }

    private async Task DeliverAsync(string tenant, DateTimeOffset now, bool allowInsecure = false)
    {
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        var guard = new OutboundAddressGuard(isBlocked: _ => false);
        using var handler = OutboundHttpHandler.Create(guard);
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [WebhookSigning.AllowInsecureSignedUrlsKey] = allowInsecure ? "true" : "false",
        }).Build();

        var transport = new SignedHttpOutboxTransport(
            new SingleClientFactory(client), _fixture.Services.GetRequiredService<ISecretProtector>(), guard, configuration,
            NullLogger<SignedHttpOutboxTransport>.Instance);
        var sender = new OutboxSender(store, transport, configuration, NullLogger<OutboxSender>.Instance);

        await using var session = store.LightweightSession(tenant);
        await sender.DeliverDueAsync(session, now, TestContext.Current.CancellationToken);
    }

    private async Task<OutboxMessage> LoadAsync(string tenant, Guid id)
    {
        await using var session = _fixture.Services.GetRequiredService<IDocumentStore>().QuerySession(tenant);
        return (await session.LoadAsync<OutboxMessage>(id, TestContext.Current.CancellationToken))!;
    }

    private async Task RepointAsync(string tenant, Guid id, string url)
    {
        await using var session = _fixture.Services.GetRequiredService<IDocumentStore>().LightweightSession(tenant);
        var message = (await session.LoadAsync<OutboxMessage>(id, TestContext.Current.CancellationToken))!;
        message.Url = url;
        session.Update(message);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static string Recipe(string secret, string timestamp, byte[] body)
    {
        var material = Encoding.UTF8.GetBytes(timestamp + ".").Concat(body).ToArray();
        return "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), material));
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}
