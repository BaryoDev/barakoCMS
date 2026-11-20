using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BarakoCMS.Tests.Features.Tenants;
using barakoCMS.Features.Workflows;
using barakoCMS.Features.Workflows.Actions;
using barakoCMS.Features.Workflows.Outbox;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Filters;
using barakoCMS.Infrastructure.Http;
using barakoCMS.Infrastructure.Security;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// The Publish action: it sends only what its payload names, refuses a field that is not Public when
/// the workflow is saved, signs what it sends, rides out an outage, and leaves no message behind when
/// the attempt is rolled back.
/// </summary>
[Collection("Integration1")]
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
    public async Task A_payload_naming_a_field_the_type_does_not_declare_is_refused_when_the_workflow_is_saved()
    {
        var type = "publish-undeclared-" + Guid.NewGuid().ToString("N")[..8];
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(Definition(type, ("Title", SensitivityLevel.Public)));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        var validator = scope.ServiceProvider.GetRequiredService<IWorkflowSchemaValidator>();

        var refused = await validator.ValidateAsync(
            Workflow(type, """{"x":"{{data.Ssn}}"}"""), TestContext.Current.CancellationToken);

        refused.IsValid.Should().BeFalse();
        refused.Errors.Should().NotBeEmpty();
        refused.Errors.Should().Contain(e => e.Message.Contains("Ssn") && e.Field == "actions[0].parameters.Payload");
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

    private const string TitlePayload = """{"title":"{{data.Title}}"}""";

    [Fact]
    public async Task A_retry_after_the_receiver_answers_500_writes_back_once()
    {
        var site = await WriteBackSiteAsync();
        using var receiver = site.Receiver(sendKey: true, answer: call => call == 0 ? 500 : 200);
        var id = await QueueAsync(site.Tenant, site.Type, receiver.Url, TitlePayload, secret: null);

        await DeliverAsync(site.Tenant, DateTimeOffset.UtcNow);
        var delayed = await LoadAsync(site.Tenant, id);
        delayed.Status.Should().Be(OutboxStatus.Pending, "the 500 left the message for a retry");
        await DeliverAsync(site.Tenant, delayed.NextAttemptAt.AddSeconds(1));

        (await LoadAsync(site.Tenant, id)).Status.Should().Be(OutboxStatus.Delivered);
        receiver.Error.Should().BeNull();
        receiver.Writes.Should().HaveCount(2, "the receiver wrote on both deliveries");
        receiver.Writes.Select(w => w.Key).Should().OnlyContain(k => k == id.ToString());
        receiver.Writes[1].Replayed.Should().BeTrue("the second write is the first one's response again");
        receiver.Writes.Select(w => w.EntryId).Distinct().Should().HaveCount(1);
        (await EntriesAsync(site.Tenant, site.Type)).Should().Be(1);
    }

    [Fact]
    public async Task The_sender_run_twice_for_one_outbox_row_writes_back_once()
    {
        var site = await WriteBackSiteAsync();
        using var receiver = site.Receiver(sendKey: true);
        var id = await QueueAsync(site.Tenant, site.Type, receiver.Url, TitlePayload, secret: null);

        await DeliverAsync(site.Tenant, DateTimeOffset.UtcNow);
        (await LoadAsync(site.Tenant, id)).Status.Should().Be(OutboxStatus.Delivered);

        // A sender that died after the receiver answered, before it saved the result, sends it again.
        await ResetToPendingAsync(site.Tenant, id);
        await DeliverAsync(site.Tenant, DateTimeOffset.UtcNow);

        receiver.Error.Should().BeNull();
        receiver.Writes.Should().HaveCount(2);
        receiver.Writes[1].Replayed.Should().BeTrue();
        receiver.Writes.Select(w => w.EntryId).Distinct().Should().HaveCount(1);
        (await EntriesAsync(site.Tenant, site.Type)).Should().Be(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Two_deliveries_of_one_message_write_back_once_in_either_arrival_order(bool firstToArriveWritesLast)
    {
        var site = await WriteBackSiteAsync();
        // Holding the first delivery back flips which write reaches the API first, so one case has the
        // later delivery win the key and the other the earlier one. Either way the second write must
        // end in the first one's entry. Whether the two ever overlap is not forced here.
        using var receiver = site.Receiver(
            sendKey: true,
            wait: call => firstToArriveWritesLast && call == 0 ? TimeSpan.FromMilliseconds(300) : TimeSpan.Zero);
        var id = await QueueAsync(site.Tenant, site.Type, receiver.Url, TitlePayload, secret: null);

        var results = await Task.WhenAll(SendOnceAsync(site.Tenant, id), SendOnceAsync(site.Tenant, id));

        results.Should().HaveCount(2);
        results.Should().OnlyContain(r => r.Delivered);
        receiver.Error.Should().BeNull();
        receiver.Writes.Should().HaveCount(2);
        receiver.Writes.Should().OnlyContain(w => w.EntryId != null, "each write ended in the one entry or its replay");
        receiver.Writes.Select(w => w.EntryId).Distinct().Should().HaveCount(1);
        (await EntriesAsync(site.Tenant, site.Type)).Should().Be(1);
    }

    [Fact]
    public async Task A_delivery_that_arrives_while_the_first_write_is_running_gets_409_then_writes_back_once()
    {
        var site = await WriteBackSiteAsync();
        using var receiver = site.Receiver(sendKey: true);
        var id = await QueueAsync(site.Tenant, site.Type, receiver.Url, TitlePayload, secret: null);

        // A first write that has claimed the key and not finished: the record is stored, not completed.
        var key = ScopedKey(site.Tenant, site.KeyId, id.ToString());
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        await using (var session = store.LightweightSession())
        {
            session.Store(new IdempotencyRecord { Key = key, ClaimId = Guid.NewGuid(), Completed = false, CreatedAt = DateTime.UtcNow, Method = "POST" });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var delivery = SendOnceAsync(site.Tenant, id);
        await WaitUntilAsync(() => receiver.Conflicts >= 1, "the write to be answered 409 while the key is in progress");

        // The first write finishes: its claim goes away and the retry takes the key.
        await using (var session = store.LightweightSession())
        {
            session.Delete<IdempotencyRecord>(key);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var result = await delivery;

        result.Delivered.Should().BeTrue();
        receiver.Error.Should().BeNull();
        receiver.Conflicts.Should().BeGreaterThanOrEqualTo(1, "the write met the key in progress");
        receiver.Writes.Should().HaveCount(1);
        receiver.Writes[0].Key.Should().Be(id.ToString());
        (await EntriesAsync(site.Tenant, site.Type)).Should().Be(1);
    }

    [Fact]
    public async Task A_workflow_run_with_a_publish_step_redelivered_writes_back_once()
    {
        var site = await WriteBackSiteAsync();
        using var receiver = site.Receiver(sendKey: true);
        var content = Entry(site.Type);
        var run = new WorkflowRun
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = Guid.NewGuid(),
            WorkflowName = "Publish run",
            ContentId = content.Id,
            ContentType = content.ContentType,
            TriggerEvent = "Published",
            TriggeringEventSequence = 1,
        };
        var parameters = Parameters(receiver.Url, TitlePayload, null);
        parameters["IdempotencyKey"] = $"{run.Id:N}-0";
        run.Actions.Add(new WorkflowActionAttempt
        {
            Ordinal = 0,
            ActionType = "Publish",
            Parameters = parameters,
            IdempotencyKey = $"{run.Id:N}-0",
        });
        run.Recompute();

        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        await _fixture.WithWorkflowRunnersPausedAsync(async () =>
        {
            await using (var session = store.LightweightSession(site.Tenant))
            {
                session.Store(content);
                session.Store(run);
                await session.SaveChangesAsync(TestContext.Current.CancellationToken);
            }

            var runner = new WorkflowRunner(
                _fixture.Services,
                _fixture.Services.GetRequiredService<ILogger<WorkflowRunner>>(),
                _fixture.Services.GetRequiredService<IConfiguration>());
            var polls = 0;
            while (await runner.RunOnceAsync(TestContext.Current.CancellationToken))
            {
                (++polls).Should().BeLessThan(200);
            }
        }, TestContext.Current.CancellationToken);

        await using var check = store.QuerySession(site.Tenant);
        var messages = await check.Query<OutboxMessage>().Where(m => m.ContentId == content.Id).ToListAsync(TestContext.Current.CancellationToken);
        messages.Should().HaveCount(1, "the run queued one message");
        var id = messages[0].Id;

        await DeliverAsync(site.Tenant, DateTimeOffset.UtcNow);
        (await LoadAsync(site.Tenant, id)).Status.Should().Be(OutboxStatus.Delivered);
        await ResetToPendingAsync(site.Tenant, id);
        await DeliverAsync(site.Tenant, DateTimeOffset.UtcNow);

        receiver.Error.Should().BeNull();
        receiver.Writes.Should().HaveCount(2);
        receiver.Writes.Select(w => w.Key).Should().OnlyContain(k => k == id.ToString(), "the key is the message id the runner produced");
        receiver.Writes[1].Replayed.Should().BeTrue();
        (await EntriesAsync(site.Tenant, site.Type)).Should().Be(2, "the run's own entry is of the same type, plus one write-back");
    }

    /// <summary>The key the API stores for this tenant, API key and raw Idempotency-Key.</summary>
    private static string ScopedKey(string tenant, Guid apiKeyId, string rawKey)
    {
        var services = new ServiceCollection().AddSingleton(new barakoCMS.Infrastructure.Multitenancy.TenantContext { Slug = tenant }).BuildServiceProvider();
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext
        {
            RequestServices = services,
            User = new System.Security.Claims.ClaimsPrincipal(
                new System.Security.Claims.ClaimsIdentity([new System.Security.Claims.Claim("apikey_id", apiKeyId.ToString())], "ApiKey")),
        };
        return IdempotencyKeyScope.Build(http, rawKey);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Timed out waiting for " + what);
            }

            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task A_receiver_that_writes_back_without_the_key_makes_an_entry_per_delivery()
    {
        var site = await WriteBackSiteAsync();
        using var receiver = site.Receiver(sendKey: false, answer: call => call == 0 ? 500 : 200);
        var id = await QueueAsync(site.Tenant, site.Type, receiver.Url, TitlePayload, secret: null);

        await DeliverAsync(site.Tenant, DateTimeOffset.UtcNow);
        var delayed = await LoadAsync(site.Tenant, id);
        await DeliverAsync(site.Tenant, delayed.NextAttemptAt.AddSeconds(1));

        receiver.Error.Should().BeNull();
        receiver.Writes.Should().HaveCount(2);
        receiver.Writes.Should().OnlyContain(w => !w.Replayed);
        receiver.Writes.Select(w => w.EntryId).Distinct().Should().HaveCount(2, "without the key nothing links the two writes");
        (await EntriesAsync(site.Tenant, site.Type)).Should().Be(2);
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

    private sealed record WriteBackSite(string Tenant, string Type, HttpClient Api, Guid KeyId)
    {
        public WritingReceiver Receiver(bool sendKey, Func<int, int>? answer = null, Func<int, TimeSpan>? wait = null) =>
            new(Api, Type, sendKey, answer ?? (_ => 200), wait ?? (_ => TimeSpan.Zero));
    }

    private sealed record WriteBack(int Call, string? Key, int Status, bool Replayed, Guid? EntryId);

    /// <summary>A tenant, a content type one API key may create entries of, and a client that holds the key.</summary>
    private async Task<WriteBackSite> WriteBackSiteAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenant = await TenantProfileSeed.TenantAsync(_fixture, ct);
        var type = $"{tenant}-inbox";
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();

        await using (var session = store.LightweightSession(tenant))
        {
            session.Store(new ContentTypeDefinition
            {
                Id = Guid.NewGuid(),
                Name = type,
                DisplayName = type,
                Fields = [new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string", IsRequired = true, Sensitivity = SensitivityLevel.Public }],
            });
            await session.SaveChangesAsync(ct);
        }

        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = $"Receivers_{Guid.NewGuid():n}",
            Permissions = [new ContentTypePermission { ContentTypeSlug = type, Create = new PermissionRule { Enabled = true } }],
        };
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = $"rx_{Guid.NewGuid():n}"[..14],
            Email = $"rx_{Guid.NewGuid():n}@example.com",
            RoleIds = [role.Id],
        };
        var secret = "bcms_" + Guid.NewGuid().ToString("N");
        var keyId = Guid.NewGuid();

        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(role);
            session.Store(user);
            session.Store(new Membership
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                TenantSlug = tenant,
                Status = MembershipStatus.Active,
                RoleIds = [role.Id],
            });
            session.Store(new ApiKey
            {
                Id = keyId,
                Name = "receiver",
                KeyHash = ApiKeyService.Hash(secret),
                Prefix = secret[..12],
                UserId = user.Id,
                TenantSlug = tenant,
                Scopes = ["content:write"],
            });
            await session.SaveChangesAsync(ct);
        }

        var api = _fixture.CreateClient();
        api.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", secret);
        api.DefaultRequestHeaders.Add("X-Tenant", tenant);
        return new WriteBackSite(tenant, type, api, keyId);
    }

    private async Task<int> EntriesAsync(string tenant, string type)
    {
        await using var session = _fixture.Services.GetRequiredService<IDocumentStore>().QuerySession(tenant);
        return await session.Query<Content>().CountAsync(c => c.ContentType == type, TestContext.Current.CancellationToken);
    }

    private async Task ResetToPendingAsync(string tenant, Guid id)
    {
        await using var session = _fixture.Services.GetRequiredService<IDocumentStore>().LightweightSession(tenant);
        var message = (await session.LoadAsync<OutboxMessage>(id, TestContext.Current.CancellationToken))!;
        message.Status = OutboxStatus.Pending;
        message.NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        message.LeasedUntil = null;
        session.Update(message);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>One send of a stored message straight through the transport, so two can run at once.</summary>
    private async Task<OutboxSendResult> SendOnceAsync(string tenant, Guid id)
    {
        var message = await LoadAsync(tenant, id);
        var guard = new OutboundAddressGuard(isBlocked: _ => false);
        using var handler = OutboundHttpHandler.Create(guard);
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        var configuration = new ConfigurationBuilder().Build();
        var transport = new SignedHttpOutboxTransport(
            new SingleClientFactory(client), _fixture.Services.GetRequiredService<ISecretProtector>(), guard, configuration,
            NullLogger<SignedHttpOutboxTransport>.Instance);

        return await transport.SendAsync(message, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// A receiver that, for each message it is sent, creates an entry through the API, with the message
    /// id as <c>Idempotency-Key</c> when asked to, and then answers the status it was told to.
    /// </summary>
    private sealed class WritingReceiver : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly ConcurrentQueue<WriteBack> _writes = new();
        private readonly HttpClient _api;
        private readonly string _type;
        private readonly bool _sendKey;
        private readonly Func<int, int> _answer;
        private readonly Func<int, TimeSpan> _wait;
        private int _calls;
        private int _conflicts;

        public WritingReceiver(HttpClient api, string type, bool sendKey, Func<int, int> answer, Func<int, TimeSpan> wait)
        {
            (_api, _type, _sendKey, _answer, _wait) = (api, type, sendKey, answer, wait);
            using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            Url = $"http://127.0.0.1:{((IPEndPoint)probe.LocalEndpoint).Port}/";
            probe.Stop();
            _listener.Prefixes.Add(Url);
            _listener.Start();
            _ = AcceptAsync();
        }

        public string Url { get; }

        /// <summary>How many writes were answered 409 because the key was still in progress.</summary>
        public int Conflicts => Volatile.Read(ref _conflicts);

        /// <summary>The first failure inside the receiver, which a test reads so a broken receiver is not mistaken for a pass.</summary>
        public string? Error { get; private set; }

        /// <summary>What each delivery wrote back, in the order the deliveries arrived.</summary>
        public IReadOnlyList<WriteBack> Writes => _writes.OrderBy(w => w.Call).ToList();

        private async Task AcceptAsync()
        {
            while (!_stopping.IsCancellationRequested)
            {
                HttpListenerContext context;
                try { context = await _listener.GetContextAsync(); }
                catch { return; }

                _ = Task.Run(() => HandleAsync(context));
            }
        }

        private async Task HandleAsync(HttpListenerContext context)
        {
            var call = Interlocked.Increment(ref _calls) - 1;
            try
            {
                var key = context.Request.Headers["Idempotency-Key"];
                using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                using var body = JsonDocument.Parse(await reader.ReadToEndAsync());
                var title = body.RootElement.GetProperty("payload").GetProperty("title").GetString()!;

                await Task.Delay(_wait(call));
                _writes.Enqueue(await WriteAsync(call, key, title));
                context.Response.StatusCode = _answer(call);
            }
            catch (Exception ex)
            {
                Error ??= ex.ToString();
                context.Response.StatusCode = 500;
            }
            finally
            {
                context.Response.Close();
            }
        }

        private async Task<WriteBack> WriteAsync(int call, string? key, string title)
        {
            for (var attempt = 0; ; attempt++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "/api/contents")
                {
                    Content = JsonContent.Create(new { contentType = _type, data = new { Title = title }, status = 1 }),
                };
                if (_sendKey && key is not null)
                {
                    request.Headers.Add("Idempotency-Key", key);
                }

                using var response = await _api.SendAsync(request);
                if (response.StatusCode == HttpStatusCode.Conflict && attempt < 50)
                {
                    Interlocked.Increment(ref _conflicts);
                    await Task.Delay(100);
                    continue;
                }

                var replayed = response.Headers.Contains(IdempotencyFilter.ReplayedHeader);
                Guid? entryId = null;
                if (response.IsSuccessStatusCode)
                {
                    using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                    entryId = json.RootElement.GetProperty("id").GetGuid();
                }

                return new WriteBack(call, key, (int)response.StatusCode, replayed, entryId);
            }
        }

        public void Dispose()
        {
            _stopping.Cancel();
            _listener.Close();
            _stopping.Dispose();
        }
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}
