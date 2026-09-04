using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using barakoCMS.Features.WebhookDeliveries;
using barakoCMS.Features.Workflows.Actions;
using barakoCMS.Infrastructure.Http;
using barakoCMS.Infrastructure.Security;
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
/// Issue #95: a webhook delivery is signed when the action holds a secret, every delivery leaves a
/// row behind, the secret is never handed back, and the log is swept.
/// </summary>
/// <remarks>
/// The signature is verified here by the recipe in <c>docs/webhooks.md</c>, written out rather than
/// by calling <c>WebhookSigning.Sign</c>: a test that checks the sender against the sender proves
/// only that it agrees with itself, and what a receiver needs is that the documented recipe agrees
/// with the wire.
/// </remarks>
[Collection("Sequential")]
public class WebhookDeliveryTests
{
    private const string Secret = "whsec_test_9f2c1e8b";

    private readonly IntegrationTestFixture _fixture;

    public WebhookDeliveryTests(IntegrationTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_signed_delivery_verifies_with_the_documented_recipe_and_not_with_another_secret()
    {
        using var listener = new RecordingListener();
        var sent = await SendAsync("webhook-signed", listener.Url, secret: Secret, allowInsecureSignedUrls: true);

        listener.WasCalled.Should().BeTrue();
        listener.LastHeaders.Should().ContainKey(WebhookSigning.SignatureHeader);
        listener.LastHeaders.Should().ContainKey(WebhookSigning.TimestampHeader);
        listener.LastHeaders.Should().ContainKey(WebhookSigning.DeliveryHeader);

        var signature = listener.LastHeaders[WebhookSigning.SignatureHeader];
        var timestamp = listener.LastHeaders[WebhookSigning.TimestampHeader];
        signature.Should().StartWith("sha256=");

        long.Parse(timestamp).Should().BeCloseTo(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 300,
            "the timestamp is what lets a receiver refuse a replay, so it has to be now");

        Recipe(Secret, timestamp, listener.LastBodyBytes!).Should().Be(signature,
            "the documented recipe over the exact bytes received must reproduce the header");
        Recipe("whsec_somebody_else", timestamp, listener.LastBodyBytes!).Should().NotBe(signature,
            "otherwise the signature is not bound to the secret");

        Guid.Parse(listener.LastHeaders[WebhookSigning.DeliveryHeader]).Should().Be(sent.Delivery.Id,
            "the delivery header is the id of the row, so a receiver's log and ours can be joined");
    }

    [Fact]
    public async Task A_delivery_without_a_secret_carries_no_signature_and_still_goes_out()
    {
        using var listener = new RecordingListener();
        await SendAsync("webhook-unsigned", listener.Url, secret: null);

        listener.WasCalled.Should().BeTrue("a hook with no secret keeps working the way it did");
        listener.LastHeaders.Should().NotContainKey(WebhookSigning.SignatureHeader);
        listener.LastHeaders.Should().ContainKey(WebhookSigning.DeliveryHeader);
    }

    /// <summary>
    /// The three places a secret could come back out: the workflow read, the delivery row and the
    /// application log. None of them may hold it, in plaintext or as the stored ciphertext.
    /// </summary>
    [Fact]
    public async Task The_secret_never_appears_in_the_read_response_the_delivery_row_or_the_log()
    {
        var plaintext = "whsec_never_returned_" + Guid.NewGuid().ToString("N");
        var client = await AdminClientAsync();

        var created = await client.PostAsJsonAsync("/api/workflows", new
        {
            name = "Signed hook " + Guid.NewGuid().ToString("N")[..8],
            triggerContentType = "article",
            triggerEvent = "Published",
            actions = new[]
            {
                new { type = "Webhook", parameters = new Dictionary<string, string> { ["Url"] = "https://hooks.example.com/x", ["Secret"] = plaintext } },
            },
        }, TestContext.Current.CancellationToken);

        created.StatusCode.Should().Be(HttpStatusCode.OK, await created.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var createdBody = await created.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        createdBody.Should().NotContain(plaintext, "the create response is the read shape");

        using var createdJson = JsonDocument.Parse(createdBody);
        var action = createdJson.RootElement.GetProperty("actions")[0];
        action.GetProperty("secretSet").GetBoolean().Should().BeTrue();
        action.GetProperty("parameters").TryGetProperty("Secret", out _).Should().BeFalse("a boolean stands in for the value");
        var id = createdJson.RootElement.GetProperty("id").GetGuid();

        // Stored encrypted, and decryptable with the deployment's own protector.
        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
            var stored = await session.LoadAsync<WorkflowDefinition>(id, TestContext.Current.CancellationToken);
            stored!.Actions[0].Parameters.Should().ContainKey("Secret");
            var ciphertext = stored.Actions[0].Parameters["Secret"];
            ciphertext.Should().NotBe(plaintext, "the definition holds ciphertext, not the secret");
            scope.ServiceProvider.GetRequiredService<ISecretProtector>().Unprotect(ciphertext).Should().Be(plaintext);

            // The list read, on whichever page the new workflow landed.
            var listBody = await PageHoldingAsync(client, id);
            listBody.Should().NotContain(plaintext);
            listBody.Should().NotContain(ciphertext, "ciphertext is not the secret, but the shape has nowhere to put it");
        }

        // The delivery row and the log, from a real signed send.
        using var listener = new RecordingListener();
        var logger = new CapturingLogger();
        var sent = await SendAsync("webhook-secret-hygiene", listener.Url, secret: plaintext, logger: logger, allowInsecureSignedUrls: true);

        listener.WasCalled.Should().BeTrue();
        var row = JsonSerializer.Serialize(sent.Delivery);
        row.Should().NotContain(plaintext);
        row.Should().NotContain(sent.Ciphertext!);
        sent.Delivery.RequestHeaders.Keys.Should().NotContain(WebhookSigning.SignatureHeader,
            "a signature over a known body is a hash of the secret");
        sent.Delivery.RequestHeaders.Keys.Should().Contain(WebhookSigning.DeliveryHeader, "the other headers are kept");

        logger.Lines.Should().NotBeEmpty("the action logs its outcome, so an empty capture would prove nothing");
        logger.Lines.Should().NotContain(line => line.Contains(plaintext) || line.Contains(sent.Ciphertext!));
    }

    [Fact]
    public async Task A_delivery_row_is_written_for_a_200()
    {
        var runId = Guid.NewGuid();
        var workflowId = Guid.NewGuid();
        using var listener = new RecordingListener(responseBody: "received");

        var sent = await SendAsync("webhook-row-200", listener.Url, secret: Secret, extra: new()
        {
            ["RunId"] = runId.ToString(),
            ["WorkflowId"] = workflowId.ToString(),
            ["TriggerEvent"] = "Published",
            ["Attempt"] = "1",
            ["IdempotencyKey"] = "run-row-200",
        }, allowInsecureSignedUrls: true);

        sent.Result.Succeeded.Should().BeTrue();

        var row = sent.Delivery;
        row.RunId.Should().Be(runId);
        row.WorkflowId.Should().Be(workflowId);
        row.Event.Should().Be("Published");
        row.Attempt.Should().Be(1);
        row.Url.Should().Be(listener.Url, "loopback with no query, so redaction leaves it whole");
        row.ResponseStatus.Should().Be(200);
        row.ResponseBody.Should().Be("received");
        row.Error.Should().BeNull();
        row.DurationMs.Should().BeGreaterThanOrEqualTo(0);
        row.RequestHeaders.Should().ContainKey("Idempotency-Key");
        row.RequestHeaders.Should().ContainKey("Content-Type");
        row.RequestHeaders.Should().ContainKey(WebhookSigning.TimestampHeader);
        row.RequestHeaders.Should().NotContainKey(WebhookSigning.SignatureHeader);
    }

    [Fact]
    public async Task A_signed_delivery_to_an_http_url_is_refused_and_leaves_a_permanent_failure_row()
    {
        using var listener = new RecordingListener();

        var sent = await SendAsync("webhook-signed-http", listener.Url, secret: Secret);

        listener.WasCalled.Should().BeFalse("the body and its signature must not go out in cleartext");
        sent.Result.Succeeded.Should().BeFalse();
        sent.Result.Retryable.Should().BeFalse("an http URL is the same on every attempt");
        sent.Result.Error.Should().Contain(WebhookSigning.AllowInsecureSignedUrlsKey);
        sent.Delivery.ResponseStatus.Should().BeNull();
        sent.Delivery.Error.Should().Contain("https");
    }

    [Fact]
    public async Task A_signed_webhook_with_an_http_url_is_refused_at_create()
    {
        var client = await AdminClientAsync();

        var created = await client.PostAsJsonAsync("/api/workflows", new
        {
            name = "Cleartext signed hook " + Guid.NewGuid().ToString("N")[..8],
            triggerContentType = "article",
            triggerEvent = "Published",
            actions = new[]
            {
                new { type = "Webhook", parameters = new Dictionary<string, string> { ["Url"] = "http://hooks.example.com/x", ["Secret"] = "whsec_x" } },
            },
        }, TestContext.Current.CancellationToken);

        var body = await created.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        created.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("actions[0].parameters.Url").And.Contain(WebhookSigning.AllowInsecureSignedUrlsKey,
            "the error names the setting that would allow it");
    }

    [Fact]
    public async Task A_delivery_row_is_written_for_a_connection_failure()
    {
        var closed = $"http://127.0.0.1:{ClosedPort()}/hook";

        var sent = await SendAsync("webhook-row-refused", closed, secret: null);

        sent.Result.Succeeded.Should().BeFalse();
        sent.Delivery.ResponseStatus.Should().BeNull("nothing answered");
        sent.Delivery.ResponseBody.Should().BeNull();
        sent.Delivery.Error.Should().Contain("HttpRequestException");
        sent.Delivery.Url.Should().Be(closed);
    }

    [Fact]
    public async Task A_response_body_is_kept_to_4_KB()
    {
        using var listener = new RecordingListener(statusCode: 500, responseBody: new string('x', 10_000));

        var sent = await SendAsync("webhook-row-truncated", listener.Url, secret: null);

        sent.Result.Succeeded.Should().BeFalse();
        sent.Delivery.ResponseStatus.Should().Be(500);
        sent.Delivery.ResponseBody.Should().HaveLength(WebhookDelivery.ResponseBodyLimit);
    }

    /// <summary>
    /// The receiver announces 64 MB, sends exactly the limit, then holds the connection. Reading the
    /// whole body before the cut would sit there until the client's timeout and fail the delivery.
    /// </summary>
    [Fact]
    public async Task A_response_past_the_limit_is_cut_at_4_KB_without_waiting_for_the_rest()
    {
        using var listener = new RecordingListener(
            responseBody: new string('y', WebhookDelivery.ResponseBodyLimit),
            declaredResponseLength: 64L * 1024 * 1024);

        var timer = Stopwatch.StartNew();
        var sent = await SendAsync("webhook-row-unbuffered", listener.Url, secret: null);
        timer.Stop();

        sent.Result.Succeeded.Should().BeTrue(sent.Result.Error);
        sent.Delivery.ResponseStatus.Should().Be(200);
        sent.Delivery.ResponseBody.Should().HaveLength(WebhookDelivery.ResponseBodyLimit);
        timer.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5), "the read stops at the limit, not at the end of the body");
    }

    [Fact]
    public async Task The_delivery_log_is_marked_no_store()
    {
        var client = await AdminClientAsync();

        var response = await client.GetAsync("/api/webhook-deliveries", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl.Should().NotBeNull("a response body can echo a credential, so no cache may keep the page");
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
    }

    /// <summary>
    /// The runner is what hands the action the run it belongs to. Driven end to end: a due run in
    /// its own tenant, claimed by whichever runner gets there first, leaves a row naming the run.
    /// </summary>
    /// <remarks>
    /// The URL is loopback, which the host's address guard refuses, so the row records the refusal.
    /// That is deliberate: it makes the assertion independent of which runner claimed the run and
    /// proves a refused delivery is logged too.
    /// </remarks>
    [Fact]
    public async Task A_run_executed_by_the_runner_leaves_a_row_naming_the_run()
    {
        const string tenant = "webhook-runner-row";
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();

        var content = new Content
        {
            Id = Guid.NewGuid(),
            ContentType = "article",
            Status = ContentStatus.Published,
            Sensitivity = SensitivityLevel.Public,
            Data = new Dictionary<string, object> { ["Title"] = "hello" },
        };

        var run = new WorkflowRun
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = Guid.NewGuid(),
            WorkflowName = "Runner row",
            ContentId = content.Id,
            ContentType = "article",
            TriggerEvent = "Published",
            TriggeringEventSequence = 1,
        };
        run.Actions.Add(new WorkflowActionAttempt
        {
            Ordinal = 0,
            ActionType = "Webhook",
            Parameters = new Dictionary<string, string> { ["Url"] = "http://127.0.0.1:9/hook" },
            IdempotencyKey = $"{run.Id:N}-0",
        });
        run.Recompute();

        await using (var session = store.LightweightSession(tenant))
        {
            session.Store(content);
            session.Store(run);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var runner = new barakoCMS.Features.Workflows.WorkflowRunner(
            _fixture.Services,
            _fixture.Services.GetRequiredService<ILogger<barakoCMS.Features.Workflows.WorkflowRunner>>(),
            _fixture.Services.GetRequiredService<IConfiguration>());

        var polls = 0;
        while (await runner.RunOnceAsync(TestContext.Current.CancellationToken))
        {
            (++polls).Should().BeLessThan(200);
        }

        WebhookDelivery? row = null;
        for (var i = 0; i < 30 && row is null; i++)
        {
            await using var check = store.QuerySession(tenant);
            row = await check.Query<WebhookDelivery>()
                .FirstOrDefaultAsync(d => d.RunId == run.Id, TestContext.Current.CancellationToken);
            if (row is null) await Task.Delay(500, TestContext.Current.CancellationToken);
        }

        row.Should().NotBeNull("the runner passes the run id and the action writes it on the row");
        row!.WorkflowId.Should().Be(run.WorkflowDefinitionId);
        row.Event.Should().Be("Published");
        row.Attempt.Should().Be(1);
        row.ResponseStatus.Should().BeNull();
        row.Error.Should().Contain("not allowed", "loopback is refused by the host's guard, and the refusal is logged");
    }

    [Fact]
    public async Task The_sweep_removes_an_old_delivery_and_keeps_a_young_one()
    {
        const string tenant = "webhook-retention";
        var now = new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();

        var old = Row(now.AddDays(-31));
        var young = Row(now.AddDays(-29));

        await using var session = store.LightweightSession(tenant);
        session.Store(old, young);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        var removed = await WebhookDeliveryRetentionService.SweepTenantAsync(
            session, now, WebhookDeliveryRetentionService.DefaultRetentionDays, TestContext.Current.CancellationToken);

        removed.Should().BeGreaterThanOrEqualTo(1);
        (await session.LoadAsync<WebhookDelivery>(old.Id, TestContext.Current.CancellationToken)).Should().BeNull("thirty-one days is past the default window");
        (await session.LoadAsync<WebhookDelivery>(young.Id, TestContext.Current.CancellationToken)).Should().NotBeNull("twenty-nine is inside it");
    }

    [Fact]
    public async Task Zero_retention_days_keeps_everything()
    {
        const string tenant = "webhook-retention-forever";
        var now = new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();

        var ancient = Row(now.AddDays(-400));
        await using var session = store.LightweightSession(tenant);
        session.Store(ancient);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        await WebhookDeliveryRetentionService.SweepTenantAsync(session, now, 0, TestContext.Current.CancellationToken);

        (await session.LoadAsync<WebhookDelivery>(ancient.Id, TestContext.Current.CancellationToken)).Should().NotBeNull();
    }

    [Fact]
    public async Task A_role_created_at_runtime_holding_view_workflow_runs_lists_deliveries()
    {
        var client = await CallerHoldingAsync(SystemCapabilities.ViewWorkflowRuns);

        var response = await client.GetAsync("/api/webhook-deliveries", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_role_holding_only_the_authoring_capability_is_refused()
    {
        var client = await CallerHoldingAsync(SystemCapabilities.ManageWorkflows);

        var response = await client.GetAsync("/api/webhook-deliveries", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "writing a workflow and reading what its hooks got back are different rights");
    }

    [Fact]
    public async Task The_list_filters_by_workflow_and_by_status_class()
    {
        var workflowId = Guid.NewGuid();
        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(
                Row(DateTimeOffset.UtcNow.AddMinutes(-3), workflowId, status: 200),
                Row(DateTimeOffset.UtcNow.AddMinutes(-2), workflowId, status: 503),
                Row(DateTimeOffset.UtcNow.AddMinutes(-1), workflowId, status: null),
                Row(DateTimeOffset.UtcNow, Guid.NewGuid(), status: 200));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var client = await AdminClientAsync();

        (await CountAsync(client, $"/api/webhook-deliveries?workflowId={workflowId}")).Should().Be(3);
        (await CountAsync(client, $"/api/webhook-deliveries?workflowId={workflowId}&status=2xx")).Should().Be(1);
        (await CountAsync(client, $"/api/webhook-deliveries?workflowId={workflowId}&status=5xx")).Should().Be(1);
        (await CountAsync(client, $"/api/webhook-deliveries?workflowId={workflowId}&status=failed")).Should().Be(1);

        var bogus = await client.GetAsync($"/api/webhook-deliveries?workflowId={workflowId}&status=ok", TestContext.Current.CancellationToken);
        bogus.StatusCode.Should().Be(HttpStatusCode.BadRequest, "an unknown class is refused rather than ignored");
    }

    /// <summary>The recipe from docs/webhooks.md, as a receiver would write it.</summary>
    private static string Recipe(string secret, string timestamp, byte[] body)
    {
        var material = Encoding.UTF8.GetBytes(timestamp + ".").Concat(body).ToArray();
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return "sha256=" + Convert.ToHexStringLower(hmac.ComputeHash(material));
    }

    private sealed record Sent(barakoCMS.Features.Workflows.WorkflowActionResult Result, WebhookDelivery Delivery, string? Ciphertext);

    /// <summary>
    /// The listener is loopback over http, so a signed send here is the lab case the opt-in exists
    /// for. A test that wants the production refusal leaves <paramref name="allowInsecureSignedUrls"/> off.
    /// </summary>
    private async Task<Sent> SendAsync(
        string tenant, string url, string? secret, Dictionary<string, string>? extra = null, ILogger<WebhookAction>? logger = null,
        bool allowInsecureSignedUrls = false)
    {
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        var protector = _fixture.Services.GetRequiredService<ISecretProtector>();
        var guard = new OutboundAddressGuard(isBlocked: _ => false);

        var parameters = new Dictionary<string, string>(extra ?? new()) { ["Url"] = url };
        string? ciphertext = null;
        if (secret is not null)
        {
            ciphertext = protector.Protect(secret);
            parameters[WebhookSigning.SecretParameter] = ciphertext;
        }

        var content = new Content
        {
            Id = Guid.NewGuid(),
            ContentType = $"{tenant}-record",
            Status = ContentStatus.Published,
            Sensitivity = SensitivityLevel.Public,
            Data = new Dictionary<string, object> { ["Title"] = "hello" },
        };

        await using var session = store.LightweightSession(tenant);
        using var handler = OutboundHttpHandler.Create(guard);
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [WebhookSigning.AllowInsecureSignedUrlsKey] = allowInsecureSignedUrls ? "true" : "false",
        }).Build();

        var action = new WebhookAction(
            new SingleClientFactory(client), session, protector, guard, logger ?? NullLogger<WebhookAction>.Instance, configuration);

        var result = await action.RunAsync(parameters, content, TestContext.Current.CancellationToken);

        await using var check = store.QuerySession(tenant);
        var rows = await check.Query<WebhookDelivery>()
            .OrderByDescending(d => d.CreatedAt)
            .ToListAsync(TestContext.Current.CancellationToken);
        rows.Should().NotBeEmpty("every delivery, sent or not, leaves a row");

        return new Sent(result, rows[0], ciphertext);
    }

    private static WebhookDelivery Row(DateTimeOffset createdAt, Guid? workflowId = null, int? status = 200) => new()
    {
        Id = Guid.NewGuid(),
        WorkflowId = workflowId ?? Guid.NewGuid(),
        Url = "https://hooks.example.com/x",
        Event = "Published",
        ResponseStatus = status,
        Error = status is null ? "The request could not be delivered (HttpRequestException)." : null,
        CreatedAt = createdAt,
    };

    private static async Task<int> CountAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK, path);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return json.RootElement.GetProperty("totalItems").GetInt32();
    }

    /// <summary>The list page holding the workflow, as raw text, so an assertion covers the whole envelope.</summary>
    private static async Task<string> PageHoldingAsync(HttpClient client, Guid id)
    {
        for (var page = 1; page <= 50; page++)
        {
            var response = await client.GetAsync($"/api/workflows?page={page}&pageSize=100", TestContext.Current.CancellationToken);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            if (body.Contains(id.ToString(), StringComparison.OrdinalIgnoreCase)) return body;

            using var json = JsonDocument.Parse(body);
            if (!json.RootElement.GetProperty("hasNextPage").GetBoolean()) break;
        }

        throw new Xunit.Sdk.XunitException("the created workflow was not on any page of the list");
    }

    private static int ClosedPort()
    {
        using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private async Task<HttpClient> AdminClientAsync()
    {
        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await _fixture.StoredUserTokenAsync("SuperAdmin", "Admin"));
        return client;
    }

    /// <summary>A role invented here, so only the capability can be what admits it.</summary>
    private async Task<HttpClient> CallerHoldingAsync(params string[] capabilities)
    {
        var unique = $"Delivery Reader {Guid.NewGuid():N}";

        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var role = new Role { Id = Guid.NewGuid(), Name = unique, SystemCapabilities = capabilities.ToList() };
        session.Store(role);

        var userId = Guid.NewGuid();
        session.Store(new User
        {
            Id = userId,
            Username = $"deliveries-{userId}",
            Email = $"deliveries-{userId}@example.com",
            RoleIds = [role.Id],
        });
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _fixture.CreateToken(roles: [unique], userId: userId.ToString()));
        return client;
    }

    private sealed class SingleClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;
        public SingleClientFactory(HttpClient client) => _client = client;
        public HttpClient CreateClient(string name) => _client;
    }

    private sealed class CapturingLogger : ILogger<WebhookAction>
    {
        public List<string> Lines { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add(formatter(state, exception) + (exception is null ? string.Empty : " " + exception));
    }
}
