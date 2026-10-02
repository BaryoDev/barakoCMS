using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using barakoCMS.Features.Workflows.Actions;
using barakoCMS.Infrastructure.Http;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Infrastructure.Security;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// What a webhook delivery actually puts on the wire: the content type's Public fields only, and
/// never to an address the guard blocks.
/// </summary>
/// <remarks>
/// The action is driven against a real listener and the assertions are on the received body and on
/// whether a socket was opened at all, rather than on the payload object the action builds. A
/// projection can be correct in the object and wrong by the time it is serialised.
/// </remarks>
[Collection("Sequential")]
public class WebhookPayloadTests
{
    private const string Ssn = "123-45-6789";
    private const string BirthDay = "1990-05-15";

    private readonly IntegrationTestFixture _fixture;

    public WebhookPayloadTests(IntegrationTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_webhook_payload_carries_the_public_fields_and_not_the_sensitive_ones()
    {
        const string tenant = "webhook-payload-fields";
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        var contentType = await SeedTypeAsync(store, tenant);
        var content = Record(contentType, SensitivityLevel.Public);

        using var listener = new RecordingListener();
        await SendAsync(store, tenant, content, listener.Url, PermitsLoopback);

        listener.WasCalled.Should().BeTrue("a permitted target still receives the delivery");
        listener.LastBody.Should().NotBeNull();
        listener.LastBody.Should().Contain("Sarah", "Name is a Public field, so the webhook is still useful");
        listener.LastBody.Should().NotContain(Ssn, "SSN is Hidden on the content type and a read would remove it");
        listener.LastBody.Should().NotContain(BirthDay, "BirthDay is Sensitive and there is no role behind a workflow");
    }

    [Fact]
    public async Task A_webhook_payload_for_a_sensitive_document_carries_no_data_at_all()
    {
        const string tenant = "webhook-payload-document";
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        var contentType = await SeedTypeAsync(store, tenant);
        var content = Record(contentType, SensitivityLevel.Sensitive);

        using var listener = new RecordingListener();
        await SendAsync(store, tenant, content, listener.Url, PermitsLoopback);

        listener.WasCalled.Should().BeTrue();
        listener.LastBody.Should().Contain(content.Id.ToString(), "the notification itself is still delivered");
        listener.LastBody.Should().NotContain("Sarah", "a read clears the data of a Sensitive document");
        listener.LastBody.Should().NotContain(Ssn);
    }

    [Fact]
    public async Task A_Deleted_delivery_holds_the_event_the_tenant_the_id_and_the_type_and_nothing_invented()
    {
        const string tenant = "webhook-payload-deleted";
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        var contentType = await SeedTypeAsync(store, tenant);
        var erased = new barakoCMS.Features.Workflows.ErasedContent(Guid.NewGuid(), contentType);

        using var listener = new RecordingListener();
        await SendAsync(store, tenant, erased, listener.Url, PermitsLoopback, new Dictionary<string, string>
        {
            ["Url"] = listener.Url,
            ["TriggerEvent"] = WorkflowEvents.Deleted,
        });

        listener.WasCalled.Should().BeTrue();
        using var body = System.Text.Json.JsonDocument.Parse(listener.LastBody!);
        var names = body.RootElement.EnumerateObject().Select(p => p.Name).ToList();

        names.Should().HaveCount(4, "a status of Draft and timestamps of now would describe an entry that is gone: {0}", listener.LastBody);
        names.Should().BeEquivalentTo(new[] { "event", "tenant", "contentId", "contentType" });
        body.RootElement.GetProperty("event").GetString().Should().Be("Deleted");
        body.RootElement.GetProperty("tenant").GetString().Should().Be(tenant,
            "an erasure is the delivery a renderer most needs to bind to one tenant");
        body.RootElement.GetProperty("contentId").GetGuid().Should().Be(erased.Id);
        body.RootElement.GetProperty("contentType").GetString().Should().Be(contentType);
    }

    [Fact]
    public async Task A_Published_delivery_names_its_event_and_keeps_every_field_it_had()
    {
        const string tenant = "webhook-payload-event";
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        var contentType = await SeedTypeAsync(store, tenant);
        var content = Record(contentType, SensitivityLevel.Public);

        using var listener = new RecordingListener();
        await SendAsync(store, tenant, content, listener.Url, PermitsLoopback, new Dictionary<string, string>
        {
            ["Url"] = listener.Url,
            ["TriggerEvent"] = WorkflowEvents.Published,
        });

        listener.WasCalled.Should().BeTrue();
        using var body = System.Text.Json.JsonDocument.Parse(listener.LastBody!);
        var names = body.RootElement.EnumerateObject().Select(p => p.Name).ToList();

        names.Should().HaveCount(8, "{0}", listener.LastBody);
        names.Should().BeEquivalentTo(new[] { "event", "tenant", "contentId", "contentType", "status", "data", "createdAt", "updatedAt" });
        body.RootElement.GetProperty("event").GetString().Should().Be("Published");
        body.RootElement.GetProperty("tenant").GetString().Should().Be(tenant, "not the default tenant, and not the store's name for it");
        body.RootElement.GetProperty("contentId").GetGuid().Should().Be(content.Id);
        body.RootElement.GetProperty("status").GetString().Should().Be("Published");
        body.RootElement.GetProperty("data").GetRawText().Should().Contain("Sarah");
    }

    [Fact]
    public async Task A_webhook_inside_a_conditional_on_a_Published_run_names_the_event()
    {
        const string tenant = "webhook-nested-published";
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        var contentType = await SeedTypeAsync(store, tenant);
        var content = Record(contentType, SensitivityLevel.Public);

        using var listener = new RecordingListener();
        await SendThroughConditionalAsync(store, tenant, content, listener.Url, WorkflowEvents.Published);

        listener.WasCalled.Should().BeTrue();
        using var body = System.Text.Json.JsonDocument.Parse(listener.LastBody!);
        var names = body.RootElement.EnumerateObject().Select(p => p.Name).ToList();

        names.Should().HaveCount(8, "{0}", listener.LastBody);
        body.RootElement.GetProperty("event").GetString().Should().Be("Published",
            "the child gets the run's trigger, not none and not the one it declared for itself");
        body.RootElement.GetProperty("tenant").GetString().Should().Be(tenant,
            "the child names the run's tenant, not the one its own parameters or its parent's claim");
        body.RootElement.GetProperty("status").GetString().Should().Be("Published");
    }

    [Fact]
    public async Task A_webhook_inside_a_conditional_on_a_Deleted_run_sends_the_event_the_tenant_the_id_and_the_type_only()
    {
        const string tenant = "webhook-nested-deleted";
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        var contentType = await SeedTypeAsync(store, tenant);
        var erased = new barakoCMS.Features.Workflows.ErasedContent(Guid.NewGuid(), contentType);

        using var listener = new RecordingListener();
        await SendThroughConditionalAsync(store, tenant, erased, listener.Url, WorkflowEvents.Deleted);

        listener.WasCalled.Should().BeTrue();
        using var body = System.Text.Json.JsonDocument.Parse(listener.LastBody!);
        var names = body.RootElement.EnumerateObject().Select(p => p.Name).ToList();

        names.Should().HaveCount(4, "{0}", listener.LastBody);
        names.Should().BeEquivalentTo(new[] { "event", "tenant", "contentId", "contentType" });
        body.RootElement.GetProperty("event").GetString().Should().Be("Deleted");
        body.RootElement.GetProperty("tenant").GetString().Should().Be(tenant);
        body.RootElement.GetProperty("contentId").GetGuid().Should().Be(erased.Id);
    }

    /// <summary>
    /// The nested tests above hand the Conditional one Webhook built here. This one takes both from
    /// the host the way the runner does, in a scope opened for a tenant, and reads where the child's
    /// delivery row was written. The session that stores the row is the one the body's tenant is
    /// read from, so a child built in the wrong scope would write to the default partition.
    /// </summary>
    /// <remarks>
    /// The host's address guard refuses loopback, so nothing is sent and the row records the
    /// refusal. That leaves no body or header to read, and this passes with or without the tenant
    /// in the body: it guards the wiring the field relies on.
    /// </remarks>
    [Fact]
    public async Task A_webhook_inside_a_conditional_resolved_from_a_tenant_scope_writes_its_row_in_that_tenant()
    {
        const string tenant = "webhook-tenant-scope";
        var runId = Guid.NewGuid();
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        var content = Record("webhook-tenant-scope-record", SensitivityLevel.Public);

        using (var scope = _fixture.Services.CreateScopeForTenant(tenant))
        {
            var actions = scope.ServiceProvider.GetServices<barakoCMS.Features.Workflows.IWorkflowAction>().ToList();
            var conditionals = actions.Where(a => a.Type == "Conditional").ToList();
            conditionals.Should().HaveCount(1, "the host registers one Conditional, and it is the real one");

            await conditionals[0].RunAsync(
                new Dictionary<string, string>
                {
                    ["Condition"] = $"{{{{contentType}}}} == {content.ContentType}",
                    ["ThenActions"] = System.Text.Json.JsonSerializer.Serialize(new[]
                    {
                        new
                        {
                            Type = "Webhook",
                            Parameters = new Dictionary<string, string>
                            {
                                ["Url"] = "http://127.0.0.1:9/hook",
                                ["RunId"] = runId.ToString(),
                            },
                        },
                    }),
                    ["TriggerEvent"] = WorkflowEvents.Published,
                },
                content,
                CancellationToken.None);
        }

        await using var inTenant = store.QuerySession(tenant);
        var rows = await inTenant.Query<WebhookDelivery>().Where(d => d.RunId == runId).ToListAsync();
        rows.Should().HaveCount(1, "the child was built in the tenant's scope, so its row is in the tenant's partition");
        rows[0].Error.Should().Contain("not allowed", "loopback is refused by the host's guard, and the refusal is recorded");

        await using var inDefault = store.QuerySession();
        var strays = await inDefault.Query<WebhookDelivery>().Where(d => d.RunId == runId).ToListAsync();
        strays.Should().BeEmpty("a child built in a plain scope would write here and name the default tenant");
    }

    [Fact]
    public async Task A_delivery_in_the_default_tenant_says_default_and_not_the_name_the_store_uses_for_it()
    {
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        var erased = new barakoCMS.Features.Workflows.ErasedContent(Guid.NewGuid(), "webhook-default-tenant-record");

        using var listener = new RecordingListener();
        await SendAsync(store, null, erased, listener.Url, PermitsLoopback, new Dictionary<string, string>
        {
            ["Url"] = listener.Url,
            ["TriggerEvent"] = WorkflowEvents.Deleted,
        });

        listener.WasCalled.Should().BeTrue();
        using var body = System.Text.Json.JsonDocument.Parse(listener.LastBody!);

        body.RootElement.GetProperty("tenant").GetString().Should().Be(Tenant.DefaultSlug);
        listener.LastBody.Should().NotContain(JasperFx.StorageConstants.DefaultTenantId);
        listener.LastHeaders.Should().ContainKey(WebhookSigning.TenantHeader);
        listener.LastHeaders[WebhookSigning.TenantHeader].Should().Be(Tenant.DefaultSlug);
    }

    /// <summary>
    /// The parameters are what a workflow author writes, so nothing in them may decide which tenant
    /// a delivery claims to be from.
    /// </summary>
    [Fact]
    public async Task A_tenant_written_into_the_action_parameters_does_not_reach_the_body_or_the_header()
    {
        const string tenant = "webhook-tenant-parameters";
        const string claimed = "webhook-tenant-claimed";
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        var contentType = await SeedTypeAsync(store, tenant);
        var content = Record(contentType, SensitivityLevel.Public);

        using var listener = new RecordingListener();
        await SendAsync(store, tenant, content, listener.Url, PermitsLoopback, new Dictionary<string, string>
        {
            ["Url"] = listener.Url,
            ["TriggerEvent"] = WorkflowEvents.Published,
            ["Tenant"] = claimed,
            ["tenant"] = claimed,
            ["TenantId"] = claimed,
            ["TenantSlug"] = claimed,
            [WebhookSigning.TenantHeader] = claimed,
        });

        listener.WasCalled.Should().BeTrue();
        using var body = System.Text.Json.JsonDocument.Parse(listener.LastBody!);

        body.RootElement.GetProperty("tenant").GetString().Should().Be(tenant);
        listener.LastBody.Should().NotContain(claimed);
        listener.LastHeaders.Should().ContainKey(WebhookSigning.TenantHeader);
        listener.LastHeaders[WebhookSigning.TenantHeader].Should().Be(tenant);
    }

    [Fact]
    public async Task A_tenant_name_a_header_cannot_carry_is_still_delivered_and_named_in_the_body()
    {
        const string tenant = "webhook-tenant-\u00f1";
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        var erased = new barakoCMS.Features.Workflows.ErasedContent(Guid.NewGuid(), "webhook-tenant-header-record");

        using var listener = new RecordingListener();
        await SendAsync(store, tenant, erased, listener.Url, PermitsLoopback, new Dictionary<string, string>
        {
            ["Url"] = listener.Url,
            ["TriggerEvent"] = WorkflowEvents.Deleted,
        });

        listener.WasCalled.Should().BeTrue("a header the client refuses to encode would fail the send");
        using var body = System.Text.Json.JsonDocument.Parse(listener.LastBody!);

        body.RootElement.GetProperty("tenant").GetString().Should().Be(tenant);
        listener.LastHeaders.Should().ContainKey(WebhookSigning.DeliveryHeader, "the headers were recorded, so a missing one is missing");
        listener.LastHeaders.Should().NotContainKey(WebhookSigning.TenantHeader);
    }

    /// <summary>
    /// The same entry sent from two tenants with one secret. The bodies differ only in the tenant,
    /// and neither signature verifies the other body.
    /// </summary>
    /// <remarks>
    /// Verified with the recipe from <c>docs/webhooks.md</c> written out, not with
    /// <c>WebhookSigning.Sign</c>, so it is the receiver's check that is shown to notice.
    /// </remarks>
    [Fact]
    public async Task The_signature_covers_the_tenant_so_a_body_renamed_to_another_tenant_does_not_verify()
    {
        const string secret = "whsec_tenant_binding_4c1d";
        const string tenant = "webhook-tenant-signed-a";
        const string other = "webhook-tenant-signed-b";
        var content = Record("webhook-tenant-signed-record", SensitivityLevel.Public);

        using var listener = new RecordingListener();
        await SendSignedAsync(tenant, content, listener.Url, secret);

        listener.WasCalled.Should().BeTrue();
        listener.LastHeaders.Should().ContainKey(WebhookSigning.SignatureHeader);
        listener.LastHeaders.Should().ContainKey(WebhookSigning.TimestampHeader);
        var signature = listener.LastHeaders[WebhookSigning.SignatureHeader];
        var timestamp = listener.LastHeaders[WebhookSigning.TimestampHeader];
        var sent = listener.LastBody!;

        using (var body = System.Text.Json.JsonDocument.Parse(sent))
        {
            body.RootElement.GetProperty("tenant").GetString().Should().Be(tenant);
        }

        Recipe(secret, timestamp, listener.LastBodyBytes!).Should().Be(signature,
            "the signed bytes are the bytes that hold the tenant");

        var renamed = sent.Replace($"\"tenant\":\"{tenant}\"", $"\"tenant\":\"{other}\"");
        renamed.Should().NotBe(sent, "the tenant has to be in the body for renaming it to mean anything");
        Recipe(secret, timestamp, Encoding.UTF8.GetBytes(renamed)).Should().NotBe(signature,
            "a body edited to name another tenant must not verify");

        using var otherListener = new RecordingListener();
        await SendSignedAsync(other, content, otherListener.Url, secret);

        otherListener.WasCalled.Should().BeTrue();
        otherListener.LastBody.Should().Be(renamed, "the two deliveries differ in the tenant and nothing else");
        Recipe(secret, timestamp, otherListener.LastBodyBytes!).Should().NotBe(signature,
            "one tenant's signature does not verify the same entry's delivery from another tenant");
    }

    /// <summary>The recipe from docs/webhooks.md, as a receiver would write it.</summary>
    private static string Recipe(string secret, string timestamp, byte[] body)
    {
        var material = Encoding.UTF8.GetBytes(timestamp + ".").Concat(body).ToArray();
        return "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), material));
    }

    /// <summary>
    /// A signed Published delivery over the loopback listener, which is http, so the lab opt-in is on.
    /// </summary>
    private async Task SendSignedAsync(string tenant, barakoCMS.Models.Content content, string url, string secret)
    {
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        var protector = _fixture.Services.GetRequiredService<ISecretProtector>();
        var guard = PermitsLoopback;

        await using var session = store.LightweightSession(tenant);
        using var handler = OutboundHttpHandler.Create(guard);
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [WebhookSigning.AllowInsecureSignedUrlsKey] = "true",
        }).Build();

        var action = new WebhookAction(
            new SingleClientFactory(client), session, protector, guard, NullLogger<WebhookAction>.Instance, configuration);

        var result = await action.RunAsync(
            new Dictionary<string, string>
            {
                ["Url"] = url,
                [WebhookSigning.SecretParameter] = protector.Protect(secret),
                ["TriggerEvent"] = WorkflowEvents.Published,
            },
            content,
            CancellationToken.None);

        result.Succeeded.Should().BeTrue("the signed delivery has to be sent: {0}", result.Error);
    }

    /// <summary>
    /// Sends through a Conditional whose then branch is one Webhook, the way the runner would call
    /// it. The child declares a trigger and a tenant of its own, and the parent a tenant too, none
    /// of which may reach the body.
    /// </summary>
    private static async Task SendThroughConditionalAsync(
        IDocumentStore store, string tenant, barakoCMS.Models.Content content, string url, string triggerEvent)
    {
        var guard = PermitsLoopback;
        await using var session = store.LightweightSession(tenant);
        using var handler = OutboundHttpHandler.Create(guard);
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };

        var webhook = new WebhookAction(
            new SingleClientFactory(client),
            session,
            new Moq.Mock<barakoCMS.Infrastructure.Security.ISecretProtector>().Object,
            guard,
            NullLogger<WebhookAction>.Instance);

        var children = new ServiceCollection();
        children.AddSingleton<barakoCMS.Features.Workflows.IWorkflowAction>(webhook);
        await using var provider = children.BuildServiceProvider();
        var conditional = new ConditionalAction(provider, NullLogger<ConditionalAction>.Instance);

        var result = await conditional.RunAsync(
            new Dictionary<string, string>
            {
                ["Condition"] = $"{{{{contentType}}}} == {content.ContentType}",
                ["ThenActions"] = System.Text.Json.JsonSerializer.Serialize(new[]
                {
                    new
                    {
                        Type = "Webhook",
                        Parameters = new Dictionary<string, string>
                        {
                            ["Url"] = url,
                            ["TriggerEvent"] = "Spoofed",
                            ["Tenant"] = "spoofed-by-the-child",
                        },
                    },
                }),
                ["TriggerEvent"] = triggerEvent,
                ["Tenant"] = "spoofed-by-the-parent",
            },
            content,
            CancellationToken.None);

        result.Succeeded.Should().BeTrue("the child webhook has to be sent: {0}", result.Error);
    }

    /// <summary>
    /// A name that answers with a public address for the pre-flight check and a blocked one for the
    /// connection never reaches the blocked address.
    /// </summary>
    /// <remarks>
    /// The sink is a bare TCP listener rather than an HTTP one: what is being asserted is whether a
    /// socket was opened to that address, and a raw accept records that without any dependence on the
    /// Host header matching a prefix.
    ///
    /// The paired case below flips only the address policy, so it proves the same flipping resolver
    /// does deliver when the address is permitted, and that this test is not passing because the
    /// action refuses everything.
    /// </remarks>
    [Fact]
    public async Task A_dns_answer_that_changes_after_the_check_does_not_move_the_connection()
    {
        const string tenant = "webhook-rebinding-blocked";
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        var contentType = await SeedTypeAsync(store, tenant);

        using var sink = new AcceptingListener();
        var guard = new OutboundAddressGuard(resolve: RebindingResolver(sink.Port));

        await SendAsync(store, tenant, Record(contentType, SensitivityLevel.Public), RebindUrl(sink.Port), guard);

        sink.WasConnected.Should().BeFalse(
            "the address dialled is the address the connect callback checked, not the one the pre-flight saw");
    }

    [Fact]
    public async Task The_same_changing_answer_is_delivered_when_the_address_is_permitted()
    {
        const string tenant = "webhook-rebinding-permitted";
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        var contentType = await SeedTypeAsync(store, tenant);

        using var sink = new AcceptingListener();
        var guard = new OutboundAddressGuard(resolve: RebindingResolver(sink.Port), isBlocked: _ => false);

        await SendAsync(store, tenant, Record(contentType, SensitivityLevel.Public), RebindUrl(sink.Port), guard);

        sink.WasConnected.Should().BeTrue("nothing about the delivery path is broken; only the address policy differs");
    }

    private static string RebindUrl(int port) => $"http://rebind.example:{port}/hook";

    /// <summary>Public for the first answer, then the sink's loopback address for every later one.</summary>
    private static Func<string, CancellationToken, Task<IPAddress[]>> RebindingResolver(int port)
    {
        var answered = 0;
        return (_, _) =>
        {
            var first = Interlocked.Increment(ref answered) == 1;
            return Task.FromResult(new[] { first ? IPAddress.Parse("203.0.113.10") : IPAddress.Loopback });
        };
    }

    private static OutboundAddressGuard PermitsLoopback => new(isBlocked: _ => false);

    private static async Task<string> SeedTypeAsync(IDocumentStore store, string tenant)
    {
        var name = $"{tenant}-record";
        await using var session = store.LightweightSession(tenant);
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = name,
            Fields =
            [
                new FieldDefinition { Name = "Name", Type = "string" },
                new FieldDefinition { Name = "BirthDay", Type = "datetime", Sensitivity = SensitivityLevel.Sensitive },
                new FieldDefinition { Name = "SSN", Type = "string", Sensitivity = SensitivityLevel.Hidden },
            ],
        });
        await session.SaveChangesAsync();
        return name;
    }

    private static barakoCMS.Models.Content Record(string contentType, SensitivityLevel level) => new()
    {
        Id = Guid.NewGuid(),
        ContentType = contentType,
        Status = ContentStatus.Published,
        Sensitivity = level,
        Data = new Dictionary<string, object>
        {
            { "Name", "Sarah" },
            { "BirthDay", BirthDay },
            { "SSN", Ssn },
        },
    };

    /// <summary>
    /// The idempotency key the runner computed is actually sent.
    /// </summary>
    /// <remarks>
    /// It was not. <c>WorkflowRunner</c> put <c>IdempotencyKey</c> into the resolved parameters and
    /// no action read it, so the key was dead: the retries happened, the header did not, and every
    /// comment saying a duplicate delivery would be absorbed downstream was describing something
    /// that was never on the wire. A receiver had no way to tell a retry from a second publish.
    ///
    /// Asserted on the request rather than on the parameters, because the parameters were always
    /// right. Only the listener can say whether anything left the process.
    /// </remarks>
    [Fact]
    public async Task A_webhook_carries_the_idempotency_key_the_runner_computed()
    {
        const string tenant = "webhook-idempotency";
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        var contentType = await SeedTypeAsync(store, tenant);
        var content = Record(contentType, SensitivityLevel.Public);

        using var listener = new RecordingListener();

        await SendAsync(store, tenant, content, listener.Url, PermitsLoopback, new Dictionary<string, string>
        {
            ["Url"] = listener.Url,
            ["IdempotencyKey"] = "run-1234-ordinal-2",
        });

        listener.WasCalled.Should().BeTrue();
        listener.LastHeaders.Should().ContainKey("Idempotency-Key");
        listener.LastHeaders["Idempotency-Key"].Should().Be("run-1234-ordinal-2");
    }

    [Fact]
    public async Task A_webhook_configured_without_a_key_sends_no_header_rather_than_an_empty_one()
    {
        // The pairing. An empty Idempotency-Key is worse than none: a receiver deduplicating on it
        // would treat every delivery that carried one as the same delivery.
        const string tenant = "webhook-no-idempotency";
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        var contentType = await SeedTypeAsync(store, tenant);
        var content = Record(contentType, SensitivityLevel.Public);

        using var listener = new RecordingListener();
        await SendAsync(store, tenant, content, listener.Url, PermitsLoopback);

        listener.WasCalled.Should().BeTrue();
        listener.LastHeaders.Should().NotContainKey("Idempotency-Key");
    }

    private static async Task SendAsync(
        IDocumentStore store,
        string? tenant,
        barakoCMS.Models.Content content,
        string url,
        OutboundAddressGuard guard,
        Dictionary<string, string>? parameters = null)
    {
        await using var session = tenant is null ? store.LightweightSession() : store.LightweightSession(tenant);
        using var handler = OutboundHttpHandler.Create(guard);
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };

        var action = new WebhookAction(
            new SingleClientFactory(client),
            session,
            new Moq.Mock<barakoCMS.Infrastructure.Security.ISecretProtector>().Object,
            guard,
            NullLogger<WebhookAction>.Instance);

        await action.ExecuteAsync(parameters ?? new Dictionary<string, string> { { "Url", url } }, content, CancellationToken.None);
    }

    private sealed class SingleClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;
        public SingleClientFactory(HttpClient client) => _client = client;
        public HttpClient CreateClient(string name) => _client;
    }

    /// <summary>Records whether a TCP connection was ever opened to it, and answers a bare 200.</summary>
    private sealed class AcceptingListener : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stopping = new();

        public AcceptingListener()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = AcceptAsync();
        }

        public int Port { get; }

        public bool WasConnected { get; private set; }

        private async Task AcceptAsync()
        {
            while (!_stopping.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stopping.Token);
                }
                catch
                {
                    return;
                }

                WasConnected = true;
                using (client)
                {
                    var reply = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    try
                    {
                        await client.GetStream().WriteAsync(reply, _stopping.Token);
                    }
                    catch
                    {
                        // The caller may already be gone; the accept is what this records.
                    }
                }
            }
        }

        public void Dispose()
        {
            _stopping.Cancel();
            _listener.Stop();
            _stopping.Dispose();
        }
    }
}
