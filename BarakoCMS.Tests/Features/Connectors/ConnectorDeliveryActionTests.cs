using System.Net;
using barakoCMS.Features.Workflows;
using barakoCMS.Features.Workflows.Actions;
using barakoCMS.Infrastructure.Connectors;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BarakoCMS.Tests.Features.Connectors;

/// <summary>
/// Issue #671, from the Request action: the action hands the sender the run it belongs to, and a
/// request the composer refused leaves a row too.
/// </summary>
[Collection("Sequential")]
public class ConnectorDeliveryActionTests : IAsyncLifetime
{
    private readonly IntegrationTestFixture _factory;
    private readonly List<string> _hosts = [];
    private readonly List<(string Tenant, Guid ConnectorId)> _seeded = [];

    public ConnectorDeliveryActionTests(IntegrationTestFixture factory) => _factory = factory;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var tenant in _seeded.GroupBy(s => s.Tenant))
        {
            await using var session = Store.LightweightSession(tenant.Key);

            foreach (var (_, id) in tenant)
            {
                session.DeleteWhere<WebhookDelivery>(d => d.ConnectorId == id);
                session.Delete<Connector>(id);
            }

            await session.SaveChangesAsync();
        }

        foreach (var host in _hosts) OAuthProviderStub.Forget(host);
    }

    private WebApplicationFactory<Program> Host => OAuthProviderStub.HostFor(_factory);

    private IDocumentStore Store => _factory.Services.GetRequiredService<IDocumentStore>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_request_action_leaves_a_row_naming_the_run_it_was_part_of()
    {
        var tenant = NewTenant();
        var host = Api(HttpStatusCode.OK);
        var (connector, request, content) = await SeedAsync(tenant, host, withContentType: true);

        var runId = Guid.NewGuid();
        var workflowId = Guid.NewGuid();
        var idempotencyKey = $"{runId:N}-0";

        await using var session = Store.QuerySession(tenant);
        var result = await NewAction(session).RunAsync(new Dictionary<string, string>
        {
            ["Request"] = request.Slug,
            ["RunId"] = runId.ToString(),
            ["WorkflowId"] = workflowId.ToString(),
            ["TriggerEvent"] = "Published",
            ["Attempt"] = "3",
            ["IdempotencyKey"] = idempotencyKey,
        }, content, Ct);

        result.Succeeded.Should().BeTrue("got: {0}", result.Error);
        OAuthProviderStub.CallsTo(host).Should().HaveCount(1);

        var rows = await RowsAsync(tenant, connector.Id);
        rows.Should().HaveCount(1);

        var row = rows[0];
        row.RunId.Should().Be(runId);
        row.WorkflowId.Should().Be(workflowId);
        row.Event.Should().Be("Published");
        row.Attempt.Should().Be(3);
        row.ConnectorSlug.Should().Be(connector.Slug);
        row.RequestSlug.Should().Be(request.Slug);
        row.Method.Should().Be("POST");
        row.ResponseStatus.Should().Be(200);
        row.RequestsSent.Should().Be(1);
        row.RequestHeaders.Should().ContainKey("Idempotency-Key").WhoseValue.Should().Be(
            idempotencyKey, "the key is what a receiver joins on, and it is not a credential");
        row.RequestHeaders.Should().ContainKey("X-Title").WhoseValue.Should().Be("hello");
    }

    [Fact]
    public async Task A_request_the_composer_refused_leaves_a_row_saying_why_and_sends_nothing()
    {
        var tenant = NewTenant();
        var host = Api(HttpStatusCode.OK);

        // No content type definition in this tenant, so the composer cannot check field sensitivity
        // and refuses.
        var (connector, request, content) = await SeedAsync(tenant, host, withContentType: false);
        var runId = Guid.NewGuid();

        await using var session = Store.QuerySession(tenant);
        var result = await NewAction(session).RunAsync(new Dictionary<string, string>
        {
            ["Request"] = request.Slug,
            ["RunId"] = runId.ToString(),
            ["TriggerEvent"] = "Published",
        }, content, Ct);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("has no definition");
        OAuthProviderStub.CallsTo(host).Should().BeEmpty("a refused request is not sent");

        var rows = await RowsAsync(tenant, connector.Id);
        rows.Should().HaveCount(1, "a request that was never sent is asked about as often as one that was");

        var row = rows[0];
        row.RunId.Should().Be(runId);
        row.RequestSlug.Should().Be(request.Slug);
        row.RequestsSent.Should().Be(0);
        row.ResponseStatus.Should().BeNull();
        row.Method.Should().BeNull();
        row.Url.Should().Be($"https://{host}");
        row.Error.Should().Be(result.Error);
    }

    /// <summary>
    /// A guard that passes both ways: a host may have put its own <see cref="IConnectorSender"/> in
    /// place, and the action still sends through it, once, and never hands it a refused request.
    /// </summary>
    [Fact]
    public async Task A_sender_a_host_put_in_place_is_sent_through_as_before_and_records_nothing()
    {
        var tenant = NewTenant();
        var (connector, request, content) = await SeedAsync(tenant, "unused.example", withContentType: true);
        var (_, refused, orphan) = await SeedAsync(tenant, "unused.example", withContentType: false);
        var sender = new HostSender();

        await using var session = Store.QuerySession(tenant);
        var action = new RequestAction(
            session,
            new RequestComposer(session, _factory.Services.GetRequiredService<IConfiguration>(), new QueryRunner(session)),
            sender,
            NullLogger<RequestAction>.Instance);

        var sent = await action.RunAsync(new() { ["Request"] = request.Slug }, content, Ct);
        var notSent = await action.RunAsync(new() { ["Request"] = refused.Slug }, orphan, Ct);

        sent.Succeeded.Should().BeTrue("got: {0}", sent.Error);
        notSent.Succeeded.Should().BeFalse();
        sender.Sent.Should().HaveCount(1);
        sender.Sent[0].Ok.Should().BeTrue();
        (await RowsAsync(tenant, connector.Id)).Should().BeEmpty();
    }

    /// <summary>
    /// The runner is what hands the action the run. Driven end to end: a due run in its own tenant,
    /// claimed by whichever runner gets there first, leaves a connector row naming the run.
    /// </summary>
    /// <remarks>
    /// The tenant has no content type definition, so the composer refuses and nothing is dialled.
    /// That keeps the assertion independent of which host's outbound client claimed the run.
    /// </remarks>
    [Fact]
    public async Task A_run_executed_by_the_runner_leaves_a_connector_row_naming_the_run()
    {
        var tenant = NewTenant();
        var (connector, request, content) = await SeedAsync(tenant, "runner.example", withContentType: false);

        var run = new WorkflowRun
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = Guid.NewGuid(),
            WorkflowName = "Runner connector row",
            ContentId = content.Id,
            ContentType = content.ContentType,
            TriggerEvent = "Published",
            TriggeringEventSequence = 1,
        };
        run.Actions.Add(new WorkflowActionAttempt
        {
            Ordinal = 0,
            ActionType = "Request",
            Parameters = new Dictionary<string, string> { ["Request"] = request.Slug },
            IdempotencyKey = $"{run.Id:N}-0",
        });
        run.Recompute();

        await using (var session = Store.LightweightSession(tenant))
        {
            session.Store(content);
            session.Store(run);
            await session.SaveChangesAsync(Ct);
        }

        var runner = new WorkflowRunner(
            _factory.Services,
            _factory.Services.GetRequiredService<ILogger<WorkflowRunner>>(),
            _factory.Services.GetRequiredService<IConfiguration>());

        var polls = 0;
        while (await runner.RunOnceAsync(Ct))
        {
            (++polls).Should().BeLessThan(200);
        }

        WebhookDelivery? row = null;
        for (var i = 0; i < 30 && row is null; i++)
        {
            await using var check = Store.QuerySession(tenant);
            row = await check.Query<WebhookDelivery>()
                .Where(d => d.RunId == run.Id)
                .OrderBy(d => d.CreatedAt)
                .FirstOrDefaultAsync(Ct);
            if (row is null) await Task.Delay(500, Ct);
        }

        row.Should().NotBeNull("the runner passes the run id and the action hands it to the sender");
        row!.ConnectorId.Should().Be(connector.Id);
        row.RequestSlug.Should().Be(request.Slug);
        row.WorkflowId.Should().Be(run.WorkflowDefinitionId);
        row.Event.Should().Be("Published");
        row.Attempt.Should().Be(1);
        row.RequestsSent.Should().Be(0);
        row.Error.Should().Contain("has no definition");
    }

    private RequestAction NewAction(IQuerySession session) => new(
        session,
        new RequestComposer(session, _factory.Services.GetRequiredService<IConfiguration>(), new QueryRunner(session)),
        OAuthConnectors.Sender(Host.Services, session, new ConnectorTokenCache(new TestClock())),
        NullLogger<RequestAction>.Instance);

    private static string NewTenant() => "dlva-" + Guid.NewGuid().ToString("n")[..8];

    private string Api(HttpStatusCode status)
    {
        var host = $"dlva{Guid.NewGuid().ToString("n")[..10]}.example";
        OAuthProviderStub.Route(host, (_, _) => Task.FromResult(OAuthProviderStub.Json(status, "{\"ok\":true}")));
        _hosts.Add(host);
        return host;
    }

    /// <summary>
    /// A connector with no credentials, a request through it, and an entry for the request to be
    /// composed from. The content type's definition is stored only when asked for.
    /// </summary>
    private async Task<(Connector Connector, RequestDefinition Request, barakoCMS.Models.Content Content)> SeedAsync(
        string tenant, string host, bool withContentType)
    {
        var type = "dlv" + Guid.NewGuid().ToString("n")[..10];

        var connector = new Connector
        {
            Id = Guid.NewGuid(),
            Name = "Delivery subject",
            Slug = "dlva" + Guid.NewGuid().ToString("n")[..10],
            BaseUrl = $"https://{host}",
            Auth = ConnectorAuth.None,
        };

        var request = new RequestDefinition
        {
            Id = Guid.NewGuid(),
            Name = "Post it",
            Slug = "req" + Guid.NewGuid().ToString("n")[..10],
            ConnectorSlug = connector.Slug,
            Method = "POST",
            PathTemplate = "/things",
            HeaderTemplates = new() { ["X-Title"] = "{{Title}}" },
            BodyTemplate = "{\"title\":\"{{Title}}\"}",
        };

        var content = new barakoCMS.Models.Content
        {
            Id = Guid.NewGuid(),
            ContentType = type,
            Status = ContentStatus.Published,
            Sensitivity = SensitivityLevel.Public,
            Data = new Dictionary<string, object> { ["Title"] = "hello" },
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        await using var session = Store.LightweightSession(tenant);
        session.Store(connector);
        session.Store(request);

        if (withContentType)
        {
            session.Store(new ContentTypeDefinition
            {
                Id = Guid.NewGuid(),
                Name = type,
                DisplayName = "Delivery subject",
                Fields = [new FieldDefinition { Name = "Title", Type = "string" }],
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }

        await session.SaveChangesAsync(Ct);

        _seeded.Add((tenant, connector.Id));
        return (connector, request, content);
    }

    private async Task<IReadOnlyList<WebhookDelivery>> RowsAsync(string tenant, Guid connectorId)
    {
        await using var session = Store.QuerySession(tenant);
        return await session.Query<WebhookDelivery>().Where(d => d.ConnectorId == connectorId).ToListAsync(Ct);
    }

    private sealed class HostSender : IConnectorSender
    {
        public List<ComposedRequest> Sent { get; } = [];

        public Task<ConnectorCallResult> ProbeAsync(Connector connector, CancellationToken ct) =>
            Task.FromResult(new ConnectorCallResult(true, 200, 1, null));

        public Task<ConnectorCallResult> SendAsync(
            Connector connector, ComposedRequest request, SuccessRule rule, string? successJsonPath, CancellationToken ct)
        {
            Sent.Add(request);
            return Task.FromResult(new ConnectorCallResult(true, 200, 1, null));
        }
    }
}
