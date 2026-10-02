using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Features.Connectors;

/// <summary>
/// Issue #671: <c>GET /api/connector-deliveries</c> lists what was sent through connectors, and
/// <c>GET /api/webhook-deliveries</c> goes on listing webhooks only, though both read one table.
/// </summary>
[Collection("Sequential")]
public class ConnectorDeliveryListTests
{
    private const string ConnectorRoute = "/api/connector-deliveries";
    private const string WebhookRoute = "/api/webhook-deliveries";

    private readonly IntegrationTestFixture _fixture;

    public ConnectorDeliveryListTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Red without the filter on the webhook list: it would count two rows for the workflow.
    /// </summary>
    [Fact]
    public async Task The_connector_list_holds_connector_rows_and_the_webhook_list_does_not()
    {
        var workflowId = Guid.NewGuid();
        var webhook = WebhookRow(workflowId);
        var connector = ConnectorRow(workflowId);
        await StoreAsync(webhook, connector);

        var client = await AdminClientAsync();

        using var webhooks = await GetAsync(client, $"{WebhookRoute}?workflowId={workflowId}");
        webhooks.RootElement.GetProperty("totalItems").GetInt32().Should().Be(1, "a connector row is not a webhook delivery");
        webhooks.RootElement.GetProperty("items")[0].GetProperty("id").GetGuid().Should().Be(webhook.Id);

        using var connectors = await GetAsync(client, $"{ConnectorRoute}?workflowId={workflowId}");
        connectors.RootElement.GetProperty("totalItems").GetInt32().Should().Be(1, "a webhook delivery is not a connector row");

        var item = connectors.RootElement.GetProperty("items")[0];
        item.GetProperty("id").GetGuid().Should().Be(connector.Id);
        item.GetProperty("connectorId").GetGuid().Should().Be(connector.ConnectorId!.Value);
        item.GetProperty("connectorSlug").GetString().Should().Be(connector.ConnectorSlug);
        item.GetProperty("requestSlug").GetString().Should().Be(connector.RequestSlug);
        item.GetProperty("method").GetString().Should().Be("POST");
        item.GetProperty("requestsSent").GetInt32().Should().Be(2);
        item.GetProperty("responseStatus").GetInt32().Should().Be(200);
        item.GetProperty("attempt").GetInt32().Should().Be(1);
    }

    /// <summary>
    /// A guard that passes both ways. A row written by a release before this one has no connector
    /// field at all, which is not the same stored shape as a null one, and it has to stay in the
    /// webhook list and out of the connector list.
    /// </summary>
    [Fact]
    public async Task A_row_stored_before_connector_sends_were_recorded_is_still_a_webhook_delivery()
    {
        var workflowId = Guid.NewGuid();
        var old = WebhookRow(workflowId);
        await StoreAsync(old);

        using (var scope = _fixture.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

            session.QueueSqlCommand(
                $"update {store.Options.DatabaseSchemaName}.mt_doc_webhook_deliveries "
              + "set data = data - 'ConnectorId' - 'connectorId' - 'ConnectorSlug' - 'connectorSlug' "
              + "- 'RequestSlug' - 'requestSlug' - 'Method' - 'method' - 'RequestsSent' - 'requestsSent' "
              + "where id = ?",
                old.Id);
            await session.SaveChangesAsync(Ct);

            var json = await session.Json.FindByIdAsync<WebhookDelivery>(old.Id, Ct);
            json.Should().NotBeNull();
            json.Should().NotContainEquivalentOf("connectorId", "the row has to look like one an earlier release wrote");
        }

        var client = await AdminClientAsync();

        (await CountAsync(client, $"{WebhookRoute}?workflowId={workflowId}")).Should().Be(1);
        (await CountAsync(client, $"{ConnectorRoute}?workflowId={workflowId}")).Should().Be(0);
    }

    [Fact]
    public async Task A_role_holding_view_workflow_runs_reads_the_row_and_not_the_response_body_or_the_request_headers()
    {
        var workflowId = Guid.NewGuid();
        await StoreAsync(ConnectorRow(workflowId, responseBody: "what the provider said"));

        var client = await CallerHoldingAsync(SystemCapabilities.ViewWorkflowRuns);

        using var json = await GetAsync(client, $"{ConnectorRoute}?workflowId={workflowId}");
        var items = json.RootElement.GetProperty("items");
        items.GetArrayLength().Should().Be(1, "the seeded row is the only one for this workflow id");

        items[0].GetProperty("responseStatus").GetInt32().Should().Be(200);
        items[0].GetProperty("responseBody").ValueKind.Should().Be(JsonValueKind.Null,
            "a provider's answer can carry a credential in a form the redaction did not know to look for");
        items[0].GetProperty("requestHeaders").ValueKind.Should().Be(JsonValueKind.Null,
            "a header an operator wrote is read where it is configured, which this capability does not reach");
    }

    [Fact]
    public async Task A_role_holding_both_capabilities_reads_the_response_body()
    {
        var workflowId = Guid.NewGuid();
        await StoreAsync(ConnectorRow(workflowId, responseBody: "what the provider said"));

        var client = await CallerHoldingAsync(
            SystemCapabilities.ViewWorkflowRuns, SystemCapabilities.ViewWebhookResponseBodies);

        using var json = await GetAsync(client, $"{ConnectorRoute}?workflowId={workflowId}");
        var items = json.RootElement.GetProperty("items");
        items.GetArrayLength().Should().Be(1);
        items[0].GetProperty("responseBody").GetString().Should().Be("what the provider said");
        items[0].GetProperty("requestHeaders").GetProperty("X-Trace").GetString().Should().Be("trace-1");
    }

    [Fact]
    public async Task A_role_holding_only_the_connector_capabilities_is_refused()
    {
        var client = await CallerHoldingAsync(SystemCapabilities.ViewConnectors, SystemCapabilities.ManageConnectors);

        var response = await client.GetAsync(ConnectorRoute, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "configuring a connector and reading what was sent through it are different rights");
    }

    [Fact]
    public async Task The_list_is_marked_no_store()
    {
        var client = await AdminClientAsync();

        var response = await client.GetAsync(ConnectorRoute, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl.Should().NotBeNull();
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
    }

    [Fact]
    public async Task The_list_filters_by_connector_request_run_and_status_class()
    {
        var workflowId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var slug = "conn-" + Guid.NewGuid().ToString("n")[..8];
        var request = "req-" + Guid.NewGuid().ToString("n")[..8];

        await StoreAsync(
            ConnectorRow(workflowId, status: 200, connectorSlug: slug, requestSlug: request, runId: runId),
            ConnectorRow(workflowId, status: 401, connectorSlug: slug),
            ConnectorRow(workflowId, status: null, connectorSlug: slug),
            ConnectorRow(workflowId, status: 200));

        var client = await AdminClientAsync();

        (await CountAsync(client, $"{ConnectorRoute}?workflowId={workflowId}")).Should().Be(4);
        (await CountAsync(client, $"{ConnectorRoute}?connector={slug}")).Should().Be(3);
        (await CountAsync(client, $"{ConnectorRoute}?connector={slug}&requestSlug={request}")).Should().Be(1);
        (await CountAsync(client, $"{ConnectorRoute}?runId={runId}")).Should().Be(1);
        (await CountAsync(client, $"{ConnectorRoute}?connector={slug}&status=2xx")).Should().Be(1);
        (await CountAsync(client, $"{ConnectorRoute}?connector={slug}&status=4XX")).Should().Be(1);
        (await CountAsync(client, $"{ConnectorRoute}?connector={slug}&status=failed")).Should().Be(1);

        var bogus = await client.GetAsync($"{ConnectorRoute}?connector={slug}&status=ok", Ct);
        bogus.StatusCode.Should().Be(HttpStatusCode.BadRequest, "an unknown class is refused, not ignored");
    }

    [Fact]
    public async Task The_list_is_paged_newest_first()
    {
        var workflowId = Guid.NewGuid();
        var older = ConnectorRow(workflowId, createdAt: DateTimeOffset.UtcNow.AddMinutes(-5));
        var newer = ConnectorRow(workflowId, createdAt: DateTimeOffset.UtcNow.AddMinutes(-1));
        await StoreAsync(older, newer);

        var client = await AdminClientAsync();

        using var first = await GetAsync(client, $"{ConnectorRoute}?workflowId={workflowId}&pageSize=1");
        first.RootElement.GetProperty("totalItems").GetInt32().Should().Be(2);
        first.RootElement.GetProperty("hasNextPage").GetBoolean().Should().BeTrue();

        var items = first.RootElement.GetProperty("items");
        items.GetArrayLength().Should().Be(1, "a page holds what was asked for, not the table");
        items[0].GetProperty("id").GetGuid().Should().Be(newer.Id);

        using var second = await GetAsync(client, $"{ConnectorRoute}?workflowId={workflowId}&pageSize=1&page=2");
        second.RootElement.GetProperty("items")[0].GetProperty("id").GetGuid().Should().Be(older.Id);
    }

    /// <summary>
    /// The same workflow id in two tenants. A caller in the default tenant is shown its own row and
    /// not the other tenant's.
    /// </summary>
    [Fact]
    public async Task A_row_in_another_tenant_is_not_listed()
    {
        var workflowId = Guid.NewGuid();
        var mine = ConnectorRow(workflowId);
        var theirs = ConnectorRow(workflowId);
        await StoreAsync(mine);

        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        await using (var other = store.LightweightSession("dlvl-" + Guid.NewGuid().ToString("n")[..8]))
        {
            other.Store(theirs);
            await other.SaveChangesAsync(Ct);
        }

        var client = await AdminClientAsync();

        using var json = await GetAsync(client, $"{ConnectorRoute}?workflowId={workflowId}");
        json.RootElement.GetProperty("totalItems").GetInt32().Should().Be(1);
        json.RootElement.GetProperty("items")[0].GetProperty("id").GetGuid().Should().Be(mine.Id);
    }

    private static WebhookDelivery WebhookRow(Guid workflowId) => new()
    {
        Id = Guid.NewGuid(),
        WorkflowId = workflowId,
        Url = "https://hooks.example.com",
        Event = "Published",
        ResponseStatus = 200,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static WebhookDelivery ConnectorRow(
        Guid workflowId, int? status = 200, string? responseBody = null, string connectorSlug = "partner-api",
        string requestSlug = "post-it", Guid? runId = null, DateTimeOffset? createdAt = null) => new()
    {
        Id = Guid.NewGuid(),
        WorkflowId = workflowId,
        RunId = runId,
        ConnectorId = Guid.NewGuid(),
        ConnectorSlug = connectorSlug,
        RequestSlug = requestSlug,
        Method = "POST",
        Url = "https://api.example.com",
        Event = "Published",
        RequestHeaders = new() { ["X-Trace"] = "trace-1" },
        RequestsSent = 2,
        ResponseStatus = status,
        ResponseBody = responseBody,
        Error = status is null ? "The request could not be completed. The host may be unreachable, or its address is blocked." : null,
        CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
    };

    private async Task StoreAsync(params WebhookDelivery[] rows)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(rows);
        await session.SaveChangesAsync(Ct);
    }

    private static async Task<JsonDocument> GetAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(path, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, path);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
    }

    private static async Task<int> CountAsync(HttpClient client, string path)
    {
        using var json = await GetAsync(client, path);
        return json.RootElement.GetProperty("totalItems").GetInt32();
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
        var unique = $"Connector Delivery Reader {Guid.NewGuid():N}";

        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var role = new Role { Id = Guid.NewGuid(), Name = unique, SystemCapabilities = capabilities.ToList() };
        session.Store(role);

        var userId = Guid.NewGuid();
        session.Store(new User
        {
            Id = userId,
            Username = $"connector-deliveries-{userId}",
            Email = $"connector-deliveries-{userId}@example.com",
            RoleIds = [role.Id],
        });
        await session.SaveChangesAsync(Ct);

        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _fixture.CreateToken(roles: [unique], userId: userId.ToString()));
        return client;
    }
}
