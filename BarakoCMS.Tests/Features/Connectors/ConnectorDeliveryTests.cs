using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using barakoCMS.Features.WebhookDeliveries;
using barakoCMS.Infrastructure.Connectors;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Features.Connectors;

/// <summary>
/// Issue #671: a request sent through a connector leaves a delivery row, as a webhook does, and the
/// row holds no credential.
/// </summary>
/// <remarks>
/// Driven at the sender, through the stub that stands in for the outbound client, so the provider's
/// answer can quote back exactly what it was sent. Every test asserts the row exists before it
/// asserts what is not in it.
/// </remarks>
[Collection("Sequential")]
public class ConnectorDeliveryTests : IAsyncLifetime
{
    private readonly IntegrationTestFixture _factory;
    private readonly OAuthTestScope _scope;
    private readonly TestClock _clock = new();
    private readonly List<string> _hosts = [];
    private readonly List<(string Tenant, Guid ConnectorId)> _seeded = [];

    public ConnectorDeliveryTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _scope = new OAuthTestScope(factory);
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var tenant in _seeded.GroupBy(s => s.Tenant))
        {
            await using var session = Store.LightweightSession(tenant.Key);

            foreach (var (_, id) in tenant)
            {
                session.DeleteWhere<WebhookDelivery>(d => d.ConnectorId == id);
                session.DeleteWhere<ConnectorSecret>(s => s.ConnectorId == id);
                session.Delete<Connector>(id);
            }

            await session.SaveChangesAsync();
        }

        foreach (var host in _hosts) OAuthProviderStub.Forget(host);

        await _scope.DisposeAsync();
    }

    private WebApplicationFactory<Program> Host => OAuthProviderStub.HostFor(_factory);

    private IDocumentStore Store => _factory.Services.GetRequiredService<IDocumentStore>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_send_leaves_one_row_naming_the_connector_the_request_and_the_run()
    {
        var tenant = NewTenant();
        var host = Api(_ => OAuthProviderStub.Json(HttpStatusCode.OK, "{\"id\":\"created\"}"));
        var connector = await SeedAsync(tenant, host, ConnectorAuth.None);

        var context = new ConnectorDeliveryContext("post-it", Guid.NewGuid(), Guid.NewGuid(), "Published", 2);
        var composed = new ComposedRequest(
            "POST", $"https://{host}/things/42?access=query-value",
            new() { ["X-Trace"] = "trace-1" }, "{\"n\":1}", "application/json");

        await using var session = Store.QuerySession(tenant);
        var result = await Sender(session).SendAsync(connector, composed, SuccessRule.TwoHundredRange, null, context, Ct);

        result.Succeeded.Should().BeTrue("got: {0}", result.Error);

        var rows = await RowsAsync(tenant, connector.Id);
        rows.Should().HaveCount(1, "one send is one row");

        var row = rows[0];
        row.ConnectorSlug.Should().Be(connector.Slug);
        row.RequestSlug.Should().Be("post-it");
        row.WorkflowId.Should().Be(context.WorkflowId);
        row.RunId.Should().Be(context.RunId);
        row.Event.Should().Be("Published");
        row.Attempt.Should().Be(2);
        row.Method.Should().Be("POST");
        row.Url.Should().Be($"https://{host}", "the path and the query can carry a credential, as a webhook URL's can");
        row.RequestsSent.Should().Be(1);
        row.ResponseStatus.Should().Be(200);
        row.ResponseBody.Should().Be("{\"id\":\"created\"}");
        row.Error.Should().BeNull();
        row.RequestHeaders.Should().ContainKey("X-Trace").WhoseValue.Should().Be("trace-1");
        row.RequestHeaders.Should().ContainKey("Content-Type");

        (await RowsAsync(JasperFx.StorageConstants.DefaultTenantId, connector.Id)).Should().BeEmpty(
            "the row belongs to the tenant the send ran in");
    }

    /// <summary>
    /// Each way a connector authenticates, with credentials an operator also wrote into the headers
    /// and the body, against a provider that answers 401 and quotes all of it back.
    /// </summary>
    [Theory]
    [InlineData(ConnectorAuth.BearerToken)]
    [InlineData(ConnectorAuth.Basic)]
    [InlineData(ConnectorAuth.ApiKeyHeader)]
    [InlineData(ConnectorAuth.OAuth2ClientCredentials)]
    public async Task No_credential_is_in_the_stored_row(ConnectorAuth auth)
    {
        var tenant = NewTenant();
        var unique = Guid.NewGuid().ToString("n");

        var connectorSecret = $"connector-secret-{unique}";
        var grantedToken = $"granted-{unique}";
        var writtenHeader = $"written-header-{unique}";
        var writtenAuthorization = $"written-authorization-{unique}";
        var bodyPassword = $"body-password-{unique}";
        var bodyClientSecret = $"body-client-secret-{unique}";
        const string username = "svc-user";
        var basicPair = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{connectorSecret}"));

        var everything = new[]
        {
            connectorSecret, grantedToken, writtenHeader, writtenAuthorization, bodyPassword, bodyClientSecret, basicPair,
        };

        // What this provider was sent besides the Authorization header and the body, which the stub
        // hands over as they arrived. The client secret of a grant goes to the token endpoint only.
        var alsoSent = auth == ConnectorAuth.OAuth2ClientCredentials
            ? new[] { writtenHeader, writtenAuthorization }
            : new[] { writtenHeader, writtenAuthorization, connectorSecret };

        var tokenHost = Api(_ => OAuthProviderStub.Json(
            HttpStatusCode.OK, $"{{\"access_token\":\"{grantedToken}\",\"token_type\":\"Bearer\",\"expires_in\":3600}}"));

        ProviderCall? received = null;
        var host = Api(call =>
        {
            received = call;
            return OAuthProviderStub.Json(
                HttpStatusCode.Unauthorized,
                $"{{\"error\":\"bad credentials\",\"authorization\":\"{call.Authorization}\","
                + $"\"seen\":\"{string.Join(' ', alsoSent)}\",\"request\":{call.Body}}}");
        });

        var settings = auth switch
        {
            ConnectorAuth.Basic => new Dictionary<string, string> { [ConnectorSettingKeys.Username] = username },
            ConnectorAuth.ApiKeyHeader => new Dictionary<string, string> { [ConnectorSettingKeys.HeaderName] = "X-Partner-Ref" },
            ConnectorAuth.OAuth2ClientCredentials => new Dictionary<string, string>
            {
                [ConnectorSettingKeys.TokenUrl] = $"https://{tokenHost}/token",
                [ConnectorSettingKeys.ClientId] = "client-one",
            },
            _ => new Dictionary<string, string>(),
        };

        var secretKey = auth switch
        {
            ConnectorAuth.Basic => ConnectorSecretKeys.Password,
            ConnectorAuth.ApiKeyHeader => ConnectorSecretKeys.ApiKey,
            ConnectorAuth.OAuth2ClientCredentials => ConnectorSecretKeys.ClientSecret,
            _ => ConnectorSecretKeys.Token,
        };

        var connector = await SeedAsync(tenant, host, auth, settings, secretKey, connectorSecret);

        var composed = new ComposedRequest(
            "POST", $"https://{host}/things",
            new()
            {
                ["X-Trace"] = "trace-1",
                ["X-Api-Key"] = writtenHeader,
                ["Proxy-Authorization"] = $"Bearer {writtenAuthorization}",
            },
            $"{{\"title\":\"hello\",\"password\":\"{bodyPassword}\",\"login\":{{\"client_secret\":\"{bodyClientSecret}\"}}}}",
            "application/json");

        await using var session = Store.QuerySession(tenant);
        var result = await Sender(session).SendAsync(
            connector, composed, SuccessRule.TwoHundredRange, null,
            new ConnectorDeliveryContext("post-it", Guid.NewGuid(), Guid.NewGuid(), "Published", 1), Ct);

        result.StatusCode.Should().Be(401, "got: {0}", result.Error);
        received.Should().NotBeNull("the provider has to have been sent the credentials for the row to be able to leak them");

        if (auth != ConnectorAuth.ApiKeyHeader)
        {
            var sent = auth switch
            {
                ConnectorAuth.Basic => $"Basic {basicPair}",
                ConnectorAuth.OAuth2ClientCredentials => $"Bearer {grantedToken}",
                _ => $"Bearer {connectorSecret}",
            };
            received!.Authorization.Should().Be(sent, "this is the credential the provider quotes back");
        }

        var rows = await RowsAsync(tenant, connector.Id);
        rows.Should().HaveCount(1, "the token request leaves no row of its own, and the send leaves one");

        var row = rows[0];
        row.ResponseStatus.Should().Be(401);
        row.ResponseBody.Should().NotBeNull("the answer is what the row is for");
        row.ResponseBody.Should().Contain("bad credentials", "what the provider said is kept");
        row.ResponseBody.Should().Contain("hello", "a field that is not a credential is left alone");
        row.ResponseBody.Should().Contain(ConnectorDeliveryRedaction.Marker);

        row.RequestHeaders.Should().ContainKey("X-Trace").WhoseValue.Should().Be("trace-1", "an ordinary header is kept");
        row.RequestHeaders.Should().ContainKey("X-Api-Key").WhoseValue.Should().Be(ConnectorDeliveryRedaction.Marker);
        row.RequestHeaders.Should().ContainKey("Proxy-Authorization").WhoseValue.Should().Be(ConnectorDeliveryRedaction.Marker);

        var attachedTo = auth == ConnectorAuth.ApiKeyHeader ? "X-Partner-Ref" : "Authorization";
        row.RequestHeaders.Should().ContainKey(attachedTo).WhoseValue.Should().Be(
            ConnectorDeliveryRedaction.Marker,
            "the header the sender attached is named and its value is not, whatever it is called");

        var stored = JsonSerializer.Serialize(row);
        foreach (var secret in everything)
        {
            stored.Should().NotContain(secret);
        }
    }

    /// <summary>
    /// Red without the query parameters in the secret set: the stored URL was already clean, and
    /// the key came back in through the answer.
    /// </summary>
    [Fact]
    public async Task A_credential_in_the_query_is_not_stored_when_the_provider_quotes_the_url()
    {
        var tenant = NewTenant();
        var key = "query-key-" + Guid.NewGuid().ToString("n");

        var host = Api(call => OAuthProviderStub.Json(
            HttpStatusCode.NotFound,
            $"{{\"error\":\"no such route\",\"url\":\"{call.Path}?api_key={key}&page=2\"}}"));
        var connector = await SeedAsync(tenant, host, ConnectorAuth.None);

        await using var session = Store.QuerySession(tenant);
        var result = await Sender(session).SendAsync(
            connector, new ComposedRequest("GET", $"https://{host}/things?api_key={key}&page=2", new(), null, null),
            SuccessRule.TwoHundredRange, null,
            new ConnectorDeliveryContext("read-it", Guid.NewGuid(), null, "Published", 1), Ct);

        result.StatusCode.Should().Be(404, "got: {0}", result.Error);

        var rows = await RowsAsync(tenant, connector.Id);
        rows.Should().HaveCount(1);
        rows[0].Url.Should().Be($"https://{host}");
        rows[0].ResponseBody.Should().NotBeNull();
        rows[0].ResponseBody.Should().Contain("no such route");
        rows[0].ResponseBody.Should().Contain("/things?api_key=" + ConnectorDeliveryRedaction.Marker + "&page=2",
            "the rest of what the provider said about the URL is kept");
        JsonSerializer.Serialize(rows[0]).Should().NotContain(key);
    }

    /// <summary>
    /// The one path where the answer is read twice, once for the row and once for the rule. Red
    /// without the change, and red if the read for the row left nothing for the rule to read.
    /// </summary>
    [Fact]
    public async Task A_send_judged_by_a_json_path_rule_keeps_the_answer_it_was_judged_on()
    {
        var tenant = NewTenant();
        var host = Api(_ => OAuthProviderStub.Json(HttpStatusCode.OK, "{\"error\":\"quota exceeded\"}"));
        var connector = await SeedAsync(tenant, host, ConnectorAuth.None);

        await using var session = Store.QuerySession(tenant);
        var result = await Sender(session).SendAsync(
            connector, new ComposedRequest("POST", $"https://{host}/things", new(), "{}", "application/json"),
            SuccessRule.TwoHundredAndJsonPathAbsent, "error",
            new ConnectorDeliveryContext("post-it", Guid.NewGuid(), null, "Published", 1), Ct);

        result.Succeeded.Should().BeFalse("the rule read the body and found the path it must not find");
        result.StatusCode.Should().Be(200);

        var rows = await RowsAsync(tenant, connector.Id);
        rows.Should().HaveCount(1);
        rows[0].ResponseStatus.Should().Be(200);
        rows[0].ResponseBody.Should().Be("{\"error\":\"quota exceeded\"}");
        rows[0].Error.Should().Be(result.Error);
        rows[0].Error.Should().Contain("success rule");
    }

    /// <summary>
    /// What the sender does not turn into a result still reaches the caller as an exception, and
    /// still leaves a row. Red without the catch in the recording overload.
    /// </summary>
    [Fact]
    public async Task A_send_that_throws_before_a_request_goes_out_leaves_a_row_and_still_throws()
    {
        var tenant = NewTenant();
        var calls = 0;
        var host = Api(_ =>
        {
            calls++;
            return OAuthProviderStub.Json(HttpStatusCode.OK, "{}");
        });
        var connector = await SeedAsync(tenant, host, ConnectorAuth.None);

        await using var session = Store.QuerySession(tenant);
        var sender = Sender(session);

        var act = () => sender.SendAsync(
            connector, new ComposedRequest("NOT A METHOD", $"https://{host}/things", new(), null, null),
            SuccessRule.TwoHundredRange, null,
            new ConnectorDeliveryContext("post-it", Guid.NewGuid(), null, "Published", 1), Ct);

        await act.Should().ThrowAsync<FormatException>();
        calls.Should().Be(0);

        var rows = await RowsAsync(tenant, connector.Id);
        rows.Should().HaveCount(1);
        rows[0].RequestsSent.Should().Be(0);
        rows[0].ResponseStatus.Should().BeNull();
        rows[0].Error.Should().Contain(nameof(FormatException));
    }

    [Fact]
    public async Task A_send_repeated_after_a_401_is_one_row_that_counts_two_requests()
    {
        const string clientSecret = "fake-client-secret-for-the-delivery-tests";

        var provider = _scope.Provider();
        var tenant = OAuthConnectors.NewTenant();
        var connector = await _scope.SeedAsync(tenant, provider, clientSecret);
        _seeded.Add((tenant, connector.Id));

        await using var session = Store.QuerySession(tenant);
        var sender = Sender(session);
        var context = new ConnectorDeliveryContext("post-it", Guid.NewGuid(), Guid.NewGuid(), "Published", 1);

        var warm = await sender.SendAsync(connector, provider.Post(), SuccessRule.TwoHundredRange, null, context, Ct);
        warm.Succeeded.Should().BeTrue("got: {0}", warm.Error);

        provider.Revoke(1);
        _clock.Now = _clock.Now.AddMinutes(2);

        var result = await sender.SendAsync(connector, provider.Post(), SuccessRule.TwoHundredRange, null, context, Ct);
        result.Succeeded.Should().BeTrue("got: {0}", result.Error);

        provider.TokenCalls.Should().HaveCount(2);
        provider.ApiCalls.Should().HaveCount(3, "one warm send, the refused send, and its one repeat");

        var rows = await RowsAsync(tenant, connector.Id);
        rows.Should().HaveCount(2, "two calls to the sender, however many requests each took, and none for a token request");
        rows.Select(r => r.Url).Should().OnlyContain(url => url == provider.BaseUrl);

        var repeated = rows.Where(r => r.RequestsSent == 2).ToList();
        repeated.Should().HaveCount(1);
        repeated[0].ResponseStatus.Should().Be(200, "the row holds the last answer, not the 401 before it");
        repeated[0].ResponseBody.Should().Be(FakeProvider.ApiBody);
        rows.Where(r => r.RequestsSent == 1).Should().HaveCount(1);

        var stored = JsonSerializer.Serialize(rows);
        stored.Should().NotContain(provider.Token(1));
        stored.Should().NotContain(provider.Token(2));
        stored.Should().NotContain(clientSecret);
    }

    [Fact]
    public async Task A_credential_that_straddles_the_cut_is_not_stored_as_its_first_half()
    {
        var tenant = NewTenant();
        var token = "straddle-" + Guid.NewGuid().ToString("n");
        var lead = new string('x', WebhookDelivery.ResponseBodyLimit - 12);

        var host = Api(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(lead + token + new string('y', 9000), Encoding.UTF8, "text/plain"),
        });

        var connector = await SeedAsync(
            tenant, host, ConnectorAuth.BearerToken, secretKey: ConnectorSecretKeys.Token, secret: token);

        await using var session = Store.QuerySession(tenant);
        await Sender(session).SendAsync(
            connector, new ComposedRequest("GET", $"https://{host}/things", new(), null, null),
            SuccessRule.TwoHundredRange, null,
            new ConnectorDeliveryContext("read-it", Guid.NewGuid(), null, "Published", 1), Ct);

        var rows = await RowsAsync(tenant, connector.Id);
        rows.Should().HaveCount(1);

        var body = rows[0].ResponseBody;
        body.Should().NotBeNull();
        body.Should().StartWith(lead);
        body!.Length.Should().BeLessThanOrEqualTo(WebhookDelivery.ResponseBodyLimit);
        body.Should().NotContain(token[..12], "twelve characters of the token fall before the cut");
        body.Should().NotContain("y", "nothing past the cut is kept");
    }

    /// <summary>
    /// A guard for the inner half of the change: it is red when nothing is recorded at all, and
    /// passes whether or not the request and the answer are captured, since nothing was sent.
    /// </summary>
    [Fact]
    public async Task A_send_refused_before_anything_went_out_leaves_a_row_that_says_so()
    {
        var tenant = NewTenant();
        var calls = 0;
        var host = Api(_ =>
        {
            calls++;
            return OAuthProviderStub.Json(HttpStatusCode.OK, "{}");
        });

        // Bearer with no Token stored, so the credential cannot be attached.
        var connector = await SeedAsync(tenant, host, ConnectorAuth.BearerToken);

        await using var session = Store.QuerySession(tenant);
        var result = await Sender(session).SendAsync(
            connector, new ComposedRequest("POST", $"https://{host}/things", new(), "{}", "application/json"),
            SuccessRule.TwoHundredRange, null,
            new ConnectorDeliveryContext("post-it", Guid.NewGuid(), null, "Published", 1), Ct);

        result.Succeeded.Should().BeFalse();
        calls.Should().Be(0);

        var rows = await RowsAsync(tenant, connector.Id);
        rows.Should().HaveCount(1, "a send that did not happen is the one most worth a row");
        rows[0].RequestsSent.Should().Be(0);
        rows[0].ResponseStatus.Should().BeNull();
        rows[0].Error.Should().Be(result.Error);
        rows[0].Error.Should().Contain(ConnectorSecretKeys.Token);
    }

    /// <summary>
    /// Red without the catch around the write: the send would throw what the log threw.
    /// </summary>
    [Fact]
    public async Task A_row_that_cannot_be_written_does_not_change_the_outcome_of_the_send()
    {
        var tenant = NewTenant();
        var host = Api(_ => OAuthProviderStub.Json(HttpStatusCode.OK, "{}"));
        var connector = await SeedAsync(tenant, host, ConnectorAuth.None);
        var logger = new CapturingLogger();

        await using var session = Store.QuerySession(tenant);
        var sender = OAuthConnectors.Sender(
            Host.Services, session, new ConnectorTokenCache(_clock), logger, deliveries: new FailingLog());

        var result = await sender.SendAsync(
            connector, new ComposedRequest("POST", $"https://{host}/things", new(), "{}", "application/json"),
            SuccessRule.TwoHundredRange, null,
            new ConnectorDeliveryContext("post-it", Guid.NewGuid(), null, "Published", 1), Ct);

        result.Succeeded.Should().BeTrue("the request went out and was answered; got: {0}", result.Error);
        result.StatusCode.Should().Be(200);

        logger.Lines.Should().Contain(line => line.Contains("Could not record") && line.Contains(connector.Slug));
        (await RowsAsync(tenant, connector.Id)).Should().BeEmpty("the write failed, so there is no row, and that is all that is lost");
    }

    /// <summary>
    /// Red without the deadline on the write: the send would wait on the log until the test's own
    /// token gave up.
    /// </summary>
    [Fact]
    public async Task A_row_write_that_hangs_is_given_up_on()
    {
        var tenant = NewTenant();
        var host = Api(_ => OAuthProviderStub.Json(HttpStatusCode.OK, "{}"));
        var connector = await SeedAsync(tenant, host, ConnectorAuth.None);
        var logger = new CapturingLogger();

        await using var session = Store.QuerySession(tenant);
        var sender = OAuthConnectors.Sender(
            Host.Services, session, new ConnectorTokenCache(_clock), logger,
            deliveries: new HangingLog(), recordTimeout: TimeSpan.FromMilliseconds(200));

        using var patience = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        patience.CancelAfter(TimeSpan.FromSeconds(30));

        var timer = Stopwatch.StartNew();
        var result = await sender.SendAsync(
            connector, new ComposedRequest("POST", $"https://{host}/things", new(), "{}", "application/json"),
            SuccessRule.TwoHundredRange, null,
            new ConnectorDeliveryContext("post-it", Guid.NewGuid(), null, "Published", 1), patience.Token);
        timer.Stop();

        result.Succeeded.Should().BeTrue("got: {0}", result.Error);
        timer.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(20), "the write is given 200 ms here, not the caller's patience");
        logger.Lines.Should().Contain(line => line.Contains("Could not record"));
    }

    /// <summary>
    /// A guard that passes both ways: the sweep already works on the document, and a connector row
    /// is that document. It is here so a later change that gives connector rows their own storage
    /// has to bring retention with it.
    /// </summary>
    [Fact]
    public async Task The_webhook_delivery_sweep_also_clears_and_removes_connector_rows()
    {
        var tenant = NewTenant();
        var now = new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
        var connectorId = Guid.NewGuid();
        _seeded.Add((tenant, connectorId));

        var old = Row(connectorId, now.AddDays(-31), "an old answer");
        var yesterday = Row(connectorId, now.AddHours(-25), "yesterday's answer");
        var fresh = Row(connectorId, now.AddHours(-1), "a fresh answer");

        await using var session = Store.LightweightSession(tenant);
        session.Store(old, yesterday, fresh);
        await session.SaveChangesAsync(Ct);

        var cleared = await WebhookDeliveryRetentionService.ClearExpiredResponseBodiesAsync(session, now, 24, Ct);
        var removed = await WebhookDeliveryRetentionService.SweepTenantAsync(session, now, 30, Ct);

        cleared.Should().Be(2);
        removed.Should().Be(1);

        var rows = await RowsAsync(tenant, connectorId);
        rows.Should().HaveCount(2);
        rows.Single(r => r.Id == yesterday.Id).ResponseBody.Should().BeNull();
        rows.Single(r => r.Id == yesterday.Id).ResponseBodyClearedAt.Should().Be(now);
        rows.Single(r => r.Id == fresh.Id).ResponseBody.Should().Be("a fresh answer");
    }

    private static WebhookDelivery Row(Guid connectorId, DateTimeOffset createdAt, string responseBody) => new()
    {
        Id = Guid.NewGuid(),
        WorkflowId = Guid.NewGuid(),
        ConnectorId = connectorId,
        ConnectorSlug = "seeded",
        RequestSlug = "post-it",
        Method = "POST",
        Url = "https://api.example",
        Event = "Published",
        RequestsSent = 1,
        ResponseStatus = 200,
        ResponseBody = responseBody,
        CreatedAt = createdAt,
    };

    private ConnectorSender Sender(IQuerySession session) =>
        OAuthConnectors.Sender(Host.Services, session, new ConnectorTokenCache(_clock));

    private static string NewTenant() => "dlv-" + Guid.NewGuid().ToString("n")[..8];

    /// <summary>A host the stub answers for, forgotten when the test ends.</summary>
    private string Api(Func<ProviderCall, HttpResponseMessage> answer)
    {
        var host = $"dlv{Guid.NewGuid().ToString("n")[..10]}.example";
        OAuthProviderStub.Route(host, (call, _) => Task.FromResult(answer(call)));
        _hosts.Add(host);
        return host;
    }

    private async Task<Connector> SeedAsync(
        string tenant, string host, ConnectorAuth auth,
        Dictionary<string, string>? settings = null, string? secretKey = null, string? secret = null)
    {
        var connector = new Connector
        {
            Id = Guid.NewGuid(),
            Name = "Delivery subject",
            Slug = "dlv" + Guid.NewGuid().ToString("n")[..10],
            BaseUrl = $"https://{host}",
            Auth = auth,
            Settings = settings ?? new(),
            SecretKeys = secret is null || secretKey is null ? [] : [secretKey],
        };

        await using var session = Store.LightweightSession(tenant);
        session.Store(connector);

        if (secret is not null && secretKey is not null)
        {
            session.Store(new ConnectorSecret
            {
                Id = Guid.NewGuid(),
                ConnectorId = connector.Id,
                Key = secretKey,
                ProtectedValue = _factory.Services.GetRequiredService<IConnectorSecretProtector>().Protect(secret),
            });
        }

        await session.SaveChangesAsync(Ct);

        _seeded.Add((tenant, connector.Id));
        return connector;
    }

    private async Task<IReadOnlyList<WebhookDelivery>> RowsAsync(string tenant, Guid connectorId)
    {
        await using var session = Store.QuerySession(tenant);
        return await session.Query<WebhookDelivery>().Where(d => d.ConnectorId == connectorId).ToListAsync(Ct);
    }

    private sealed class FailingLog : IConnectorDeliveryLog
    {
        public Task WriteAsync(string tenantId, WebhookDelivery delivery, CancellationToken ct) =>
            throw new InvalidOperationException("the database is not taking writes");
    }

    private sealed class HangingLog : IConnectorDeliveryLog
    {
        public Task WriteAsync(string tenantId, WebhookDelivery delivery, CancellationToken ct) =>
            Task.Delay(Timeout.Infinite, ct);
    }
}
