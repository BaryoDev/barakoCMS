using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using barakoCMS.Infrastructure.Connectors;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Features.Connectors;

/// <summary>
/// A connector with OAuth2ClientCredentials: where the client secret and the token go, and where
/// they must not turn up.
/// </summary>
/// <remarks>
/// The provider is a stub in place of the ExternalApi client's primary handler, so every token
/// request is counted. Most tests build the sender themselves over one tenant's session and their
/// own cache and clock, which is what lets them count token calls exactly and move time. The ones
/// about storage, responses and the two endpoints go over HTTP, through the host's own sender.
/// </remarks>
[Collection("Sequential")]
public class ConnectorOAuthTests : IAsyncLifetime
{
    private const string Secret = "fake-client-secret-for-the-oauth-tests";

    private readonly IntegrationTestFixture _factory;
    private readonly OAuthTestScope _scope;

    public ConnectorOAuthTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _scope = new OAuthTestScope(factory);
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _scope.DisposeAsync();

    private WebApplicationFactory<Program> Host => OAuthProviderStub.HostFor(_factory);

    [Fact]
    public async Task A_send_fetches_a_token_with_the_client_credentials_and_attaches_it_as_a_bearer_header()
    {
        var provider = _scope.Provider();
        var tenant = OAuthConnectors.NewTenant();
        var connector = await _scope.SeedAsync(tenant, provider, Secret, settings: new()
        {
            [ConnectorSettingKeys.TokenUrl] = provider.TokenUrl,
            [ConnectorSettingKeys.ClientId] = OAuthConnectors.ClientIdOf(provider),
            [ConnectorSettingKeys.Scope] = "accounting.read",
            [ConnectorSettingKeys.Audience] = "https://api.example",
        });

        await using var session = Store.QuerySession(tenant);
        var sender = OAuthConnectors.Sender(Host.Services, session, new ConnectorTokenCache(new TestClock()));

        var result = await sender.SendAsync(
            connector, provider.Post(), SuccessRule.TwoHundredRange, null, TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue("got: {0}", result.Error);

        var tokenCalls = provider.TokenCalls;
        tokenCalls.Should().HaveCount(1);
        tokenCalls[0].Path.Should().Be("/token");
        tokenCalls[0].Authorization.Should().Be(OAuthConnectors.BasicFor(provider, Secret),
            "HTTP Basic is the default way a client authenticates to the token endpoint");
        tokenCalls[0].Body.Should().Contain("grant_type=client_credentials");
        tokenCalls[0].Body.Should().Contain("scope=accounting.read");
        tokenCalls[0].Body.Should().Contain("audience=https%3A%2F%2Fapi.example");
        tokenCalls[0].Body.Should().NotContain("client_secret", "with Basic the secret is in the header only");

        var apiCalls = provider.ApiCalls;
        apiCalls.Should().HaveCount(1);
        apiCalls[0].Authorization.Should().Be($"Bearer {provider.Token(1)}");
        apiCalls[0].Body.Should().Be("{\"n\":1}", "the composed request goes out as composed");
    }

    [Fact]
    public async Task Client_credentials_go_in_the_form_body_when_the_connector_asks_for_it()
    {
        var provider = _scope.Provider();
        var tenant = OAuthConnectors.NewTenant();
        var connector = await _scope.SeedAsync(tenant, provider, Secret, settings: new()
        {
            [ConnectorSettingKeys.TokenUrl] = provider.TokenUrl,
            [ConnectorSettingKeys.ClientId] = OAuthConnectors.ClientIdOf(provider),
            [ConnectorSettingKeys.ClientAuth] = ConnectorSettingKeys.ClientAuthBody,
        });

        await using var session = Store.QuerySession(tenant);
        var sender = OAuthConnectors.Sender(Host.Services, session, new ConnectorTokenCache(new TestClock()));

        var result = await sender.SendAsync(
            connector, provider.Post(), SuccessRule.TwoHundredRange, null, TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue("got: {0}", result.Error);

        var tokenCalls = provider.TokenCalls;
        tokenCalls.Should().HaveCount(1);
        tokenCalls[0].Authorization.Should().BeNull();
        tokenCalls[0].Body.Should().Contain($"client_id={OAuthConnectors.ClientIdOf(provider)}");
        tokenCalls[0].Body.Should().Contain($"client_secret={Secret}");
    }

    [Fact]
    public async Task A_second_send_inside_the_lifetime_reuses_the_token()
    {
        var provider = _scope.Provider();
        var tenant = OAuthConnectors.NewTenant();
        var connector = await _scope.SeedAsync(tenant, provider, Secret);

        await using var session = Store.QuerySession(tenant);
        var sender = OAuthConnectors.Sender(Host.Services, session, new ConnectorTokenCache(new TestClock()));

        for (var i = 0; i < 2; i++)
        {
            await SendOkAsync(sender, connector, provider);
        }

        provider.TokenCalls.Should().HaveCount(1, "the second send is inside the hour the token was granted for");

        var apiCalls = provider.ApiCalls;
        apiCalls.Should().HaveCount(2);
        apiCalls.Should().OnlyContain(c => c.Authorization == $"Bearer {provider.Token(1)}");
    }

    [Fact]
    public async Task An_expired_token_is_fetched_again_shortly_before_its_lifetime_ends()
    {
        var provider = _scope.Provider();
        provider.Expiry = ",\"expires_in\":600";
        var tenant = OAuthConnectors.NewTenant();
        var connector = await _scope.SeedAsync(tenant, provider, Secret);
        var clock = new TestClock();
        var start = clock.Now;

        await using var session = Store.QuerySession(tenant);
        var sender = OAuthConnectors.Sender(Host.Services, session, new ConnectorTokenCache(clock));

        await SendOkAsync(sender, connector, provider);

        clock.Now = start.AddSeconds(569);
        await SendOkAsync(sender, connector, provider);
        provider.TokenCalls.Should().HaveCount(1, "569 seconds into a 600 second token is still inside it");

        clock.Now = start.AddSeconds(571);
        await SendOkAsync(sender, connector, provider);
        provider.TokenCalls.Should().HaveCount(2, "the token is replaced 30 seconds before the provider's own deadline");

        var apiCalls = provider.ApiCalls;
        apiCalls.Should().HaveCount(3);
        apiCalls[2].Authorization.Should().Be($"Bearer {provider.Token(2)}");
    }

    [Theory]
    [InlineData("", 59, 61)]
    [InlineData(",\"expires_in\":0", 59, 61)]
    [InlineData(",\"expires_in\":-5", 59, 61)]
    [InlineData(",\"expires_in\":\"soon\"", 59, 61)]
    [InlineData(",\"expires_in\":315360000", 3569, 3571)]
    [InlineData(",\"expires_in\":\"600\"", 569, 571)]
    [InlineData(",\"expires_in\":600.0", 569, 571)]
    [InlineData(",\"expires_in\":600.9", 569, 571)]
    public async Task A_missing_or_unusable_lifetime_gets_a_short_one_and_a_long_one_is_capped(
        string expiry, int stillCachedAt, int fetchedAgainAt)
    {
        var provider = _scope.Provider();
        provider.Expiry = expiry;
        var tenant = OAuthConnectors.NewTenant();
        var connector = await _scope.SeedAsync(tenant, provider, Secret);
        var clock = new TestClock();
        var start = clock.Now;

        await using var session = Store.QuerySession(tenant);
        var sender = OAuthConnectors.Sender(Host.Services, session, new ConnectorTokenCache(clock));

        await SendOkAsync(sender, connector, provider);

        clock.Now = start.AddSeconds(stillCachedAt);
        await SendOkAsync(sender, connector, provider);
        provider.TokenCalls.Should().HaveCount(1);

        clock.Now = start.AddSeconds(fetchedAgainAt);
        await SendOkAsync(sender, connector, provider);
        provider.TokenCalls.Should().HaveCount(2);
    }

    [Fact]
    public async Task An_edited_connector_does_not_reuse_the_token_fetched_with_its_old_credentials()
    {
        var provider = _scope.Provider();
        var tenant = OAuthConnectors.NewTenant();
        var cache = new ConnectorTokenCache(new TestClock());
        var before = await _scope.SeedAsync(tenant, provider, Secret);

        await using (var session = Store.QuerySession(tenant))
        {
            await SendOkAsync(OAuthConnectors.Sender(Host.Services, session, cache), before, provider);
        }

        await using (var edit = Store.LightweightSession(tenant))
        {
            var stored = await edit.Query<ConnectorSecret>()
                .FirstAsync(s => s.ConnectorId == before.Id, TestContext.Current.CancellationToken);
            stored.ProtectedValue = _factory.Services.GetRequiredService<IConnectorSecretProtector>().Protect("fake-rotated-secret");
            edit.Store(stored);
            before.UpdatedAt = before.UpdatedAt.AddSeconds(1);
            edit.Store(before);
            await edit.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var session = Store.QuerySession(tenant))
        {
            await SendOkAsync(OAuthConnectors.Sender(Host.Services, session, cache), before, provider);
        }

        var tokenCalls = provider.TokenCalls;
        tokenCalls.Should().HaveCount(2, "the cached token belongs to the connector as it was before the edit");
        tokenCalls[1].Authorization.Should().Be(OAuthConnectors.BasicFor(provider, "fake-rotated-secret"));
        cache.Count.Should().Be(1, "the token for the old version is dropped when the new one is stored");
    }

    /// <summary>
    /// Both tenants hold a connector with the same id and the same version, so the tenant is the
    /// only part of the cache key that tells them apart.
    /// </summary>
    [Fact]
    public async Task Another_tenants_connector_does_not_share_a_cached_token()
    {
        var provider = _scope.Provider();
        var cache = new ConnectorTokenCache(new TestClock());
        var id = Guid.NewGuid();
        var version = DateTime.UtcNow;
        var first = OAuthConnectors.NewTenant();
        var second = OAuthConnectors.NewTenant();

        var mine = await _scope.SeedAsync(first, provider, Secret, id, version);
        var theirs = await _scope.SeedAsync(second, provider, "fake-secret-of-the-other-tenant", id, version);

        await using (var session = Store.QuerySession(first))
        {
            await SendOkAsync(OAuthConnectors.Sender(Host.Services, session, cache), mine, provider);
        }

        await using (var session = Store.QuerySession(second))
        {
            await SendOkAsync(OAuthConnectors.Sender(Host.Services, session, cache), theirs, provider);
        }

        var tokenCalls = provider.TokenCalls;
        tokenCalls.Should().HaveCount(2, "the second tenant gets a token for its own credentials, not the first tenant's");
        tokenCalls[1].Authorization.Should().Be(OAuthConnectors.BasicFor(provider, "fake-secret-of-the-other-tenant"));

        var apiCalls = provider.ApiCalls;
        apiCalls.Should().HaveCount(2);
        apiCalls[0].Authorization.Should().Be($"Bearer {provider.Token(1)}");
        apiCalls[1].Authorization.Should().Be($"Bearer {provider.Token(2)}");
    }

    [Theory]
    [InlineData(400, "{\"error\":\"invalid_client\",\"error_description\":\"ECHO was wrong\"}", "the error 'invalid_client'")]
    [InlineData(400, "{\"error\":\"ECHO\"}", "an error code that is not a standard one")]
    [InlineData(200, "{\"error\":\"invalid_scope\"}", "the error 'invalid_scope'")]
    [InlineData(403, "<html>ECHO</html>", "answered 403.")]
    [InlineData(200, "<html>ECHO</html>", "a body that is not a JSON object")]
    [InlineData(200, "[\"ECHO\"]", "a body that is not a JSON object")]
    [InlineData(200, "{\"token\":\"ECHO\"}", "without an access_token")]
    [InlineData(200, "{\"access_token\":\"two words ECHO\"}", "cannot be sent as a Bearer header")]
    [InlineData(200, "{\"access_token\":\"abc\",\"token_type\":\"mac\"}", "a token type other than Bearer")]
    [InlineData(200, "{\"access_token\":\"abc\",\"token_type\":7}", "a token type other than Bearer")]
    public async Task A_token_endpoint_that_does_not_grant_a_token_is_named_and_its_body_is_not_repeated(
        int status, string body, string expected)
    {
        var provider = _scope.Provider();
        provider.TokenAnswer = _ => OAuthProviderStub.Json((HttpStatusCode)status, body.Replace("ECHO", Secret));
        var tenant = OAuthConnectors.NewTenant();
        var connector = await _scope.SeedAsync(tenant, provider, Secret);
        var log = new CapturingLogger();

        await using var session = Store.QuerySession(tenant);
        var sender = OAuthConnectors.Sender(Host.Services, session, new ConnectorTokenCache(new TestClock()), log);

        var result = await sender.SendAsync(
            connector, provider.Post(), SuccessRule.TwoHundredRange, null, TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.StatusCode.Should().BeNull("the API was never called, so it answered nothing");
        result.Error.Should().Contain(provider.AuthHost).And.Contain(expected);
        result.Error.Should().NotContain(Secret, "an error body can echo the credential that was sent");

        provider.TokenCalls.Should().HaveCount(1);
        provider.ApiCalls.Should().BeEmpty("nothing goes to the API without a token");

        log.Lines.Should().NotBeEmpty("a refused grant is logged");
        string.Join('\n', log.Lines).Should().NotContain(Secret);
    }

    /// <summary>
    /// The client's timeouts end when the headers arrive, and the token body is read after them.
    /// The test's own patience is 20 seconds, so without the deadline this ends in a cancellation
    /// instead of a result.
    /// </summary>
    [Fact]
    public async Task A_token_endpoint_that_sends_headers_and_then_stalls_is_given_up_on_at_the_deadline()
    {
        var provider = _scope.Provider();
        provider.TokenAnswer = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream()) };
        var tenant = OAuthConnectors.NewTenant();
        var connector = await _scope.SeedAsync(tenant, provider, Secret);

        await using var session = Store.QuerySession(tenant);
        var sender = OAuthConnectors.Sender(Host.Services, session, new ConnectorTokenCache(new TestClock()),
            grantTimeout: TimeSpan.FromMilliseconds(300));

        using var patience = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        patience.CancelAfter(TimeSpan.FromSeconds(20));
        var timer = Stopwatch.StartNew();

        var result = await sender.SendAsync(connector, provider.Post(), SuccessRule.TwoHundredRange, null, patience.Token);

        timer.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15));
        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be($"The token endpoint at {provider.AuthHost} timed out.");
        provider.TokenCalls.Should().HaveCount(1);
        provider.ApiCalls.Should().BeEmpty("nothing goes to the API without a token");
    }

    /// <summary>
    /// A guard beside the deadline test: the caller giving up is still a cancellation, not a
    /// "timed out" result that a workflow run would record as the provider's failure.
    /// </summary>
    [Fact]
    public async Task A_caller_that_cancels_during_the_token_request_gets_a_cancellation_not_a_result()
    {
        var provider = _scope.Provider();
        provider.TokenAnswer = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream()) };
        var tenant = OAuthConnectors.NewTenant();
        var connector = await _scope.SeedAsync(tenant, provider, Secret);

        await using var session = Store.QuerySession(tenant);
        var sender = OAuthConnectors.Sender(Host.Services, session, new ConnectorTokenCache(new TestClock()),
            grantTimeout: TimeSpan.FromSeconds(60));

        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        caller.CancelAfter(TimeSpan.FromMilliseconds(300));

        var act = () => sender.SendAsync(connector, provider.Post(), SuccessRule.TwoHundredRange, null, caller.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    /// <summary>
    /// A connector saved with this auth kind before it did anything has no token URL. It is told
    /// what is missing, and nothing is sent.
    /// </summary>
    [Theory]
    [InlineData(null, "client", "TokenUrl")]
    [InlineData("/token", "client", "TokenUrl")]
    [InlineData("ftp://auth.example/token", "client", "TokenUrl")]
    [InlineData("https://auth.example/token", " ", "ClientId")]
    public async Task A_connector_without_the_settings_the_grant_needs_says_which_one(
        string? tokenUrl, string clientId, string named)
    {
        var provider = _scope.Provider();
        var tenant = OAuthConnectors.NewTenant();
        var settings = new Dictionary<string, string> { [ConnectorSettingKeys.ClientId] = clientId };
        if (tokenUrl is not null) settings[ConnectorSettingKeys.TokenUrl] = tokenUrl;
        var connector = await _scope.SeedAsync(tenant, provider, Secret, settings: settings);

        await using var session = Store.QuerySession(tenant);
        var sender = OAuthConnectors.Sender(Host.Services, session, new ConnectorTokenCache(new TestClock()));

        var result = await sender.SendAsync(
            connector, provider.Post(), SuccessRule.TwoHundredRange, null, TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain($"'{named}'");
        provider.ApiCalls.Should().BeEmpty();
    }

    /// <summary>
    /// A connector saved with <c>"settings": null</c> holds no dictionary at all. Each auth kind
    /// that reads a setting answers with what is missing, where it used to throw.
    /// </summary>
    [Theory]
    [InlineData(ConnectorAuth.OAuth2ClientCredentials, "'TokenUrl'")]
    [InlineData(ConnectorAuth.ApiKeyHeader, "'HeaderName'")]
    [InlineData(ConnectorAuth.Basic, "'Password'")]
    public async Task A_connector_stored_with_no_settings_is_told_what_is_missing(ConnectorAuth auth, string named)
    {
        var provider = _scope.Provider();
        var tenant = OAuthConnectors.NewTenant();
        var connector = await _scope.SeedAsync(tenant, provider, Secret);
        connector.Auth = auth;
        connector.Settings = null!;

        await using var session = Store.QuerySession(tenant);
        var sender = OAuthConnectors.Sender(Host.Services, session, new ConnectorTokenCache(new TestClock()));

        var result = await sender.ProbeAsync(connector, TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain(named);
        provider.ApiCalls.Should().BeEmpty();
    }

    /// <summary>
    /// The token URL is dialled through the same guarded client as every other outbound call. This
    /// one uses the fixture's own host, where that client's handler is the real one, and a listener
    /// on loopback that an unguarded client would reach.
    /// </summary>
    [Fact]
    public async Task A_token_url_on_a_blocked_address_is_refused_before_anything_connects()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var provider = _scope.Provider();
        var tenant = OAuthConnectors.NewTenant();
        var connector = await _scope.SeedAsync(tenant, provider, Secret, settings: new()
        {
            [ConnectorSettingKeys.TokenUrl] = $"http://127.0.0.1:{port}/token",
            [ConnectorSettingKeys.ClientId] = OAuthConnectors.ClientIdOf(provider),
        });

        await using var session = Store.QuerySession(tenant);
        var sender = OAuthConnectors.Sender(_factory.Services, session, new ConnectorTokenCache(new TestClock()),
            grantTimeout: TimeSpan.FromMinutes(2));

        var result = await sender.SendAsync(
            connector, provider.Post(), SuccessRule.TwoHundredRange, null, TestContext.Current.CancellationToken);

        listener.Pending().Should().BeFalse("the guard refuses the address before a socket is opened to it");
        result.Succeeded.Should().BeFalse();
        result.StatusCode.Should().BeNull();
        result.Error.Should().Contain("The token endpoint at 127.0.0.1 could not be reached");
        provider.ApiCalls.Should().BeEmpty();
    }

    [Fact]
    public void The_cache_holds_a_bounded_number_of_tokens_for_one_tenant()
    {
        var cache = new ConnectorTokenCache(new TestClock());

        for (var i = 0; i < ConnectorTokenCache.MaxEntriesPerTenant + 20; i++)
        {
            cache.Set(new ConnectorTokenKey("tenant", Guid.NewGuid(), 1), $"token-{i}", TimeSpan.FromMinutes(i + 1));
        }

        cache.Count.Should().Be(ConnectorTokenCache.MaxEntriesPerTenant);
    }

    /// <summary>
    /// The quiet tenant's token is the one closest to expiry in the whole cache, so a single pool
    /// would drop it first.
    /// </summary>
    [Fact]
    public void One_tenants_tokens_do_not_push_another_tenants_out()
    {
        var cache = new ConnectorTokenCache(new TestClock());
        var quiet = new ConnectorTokenKey("quiet", Guid.NewGuid(), 1);
        cache.Set(quiet, "the-quiet-tenants-token", TimeSpan.FromMinutes(1));

        for (var i = 0; i < ConnectorTokenCache.MaxEntries + 50; i++)
        {
            cache.Set(new ConnectorTokenKey("busy", Guid.NewGuid(), 1), $"token-{i}", TimeSpan.FromHours(1));
        }

        cache.Get(quiet).Should().Be("the-quiet-tenants-token");
        cache.Count.Should().Be(ConnectorTokenCache.MaxEntriesPerTenant + 1);
    }

    [Fact]
    public void The_cache_holds_a_bounded_number_of_tokens_across_tenants()
    {
        var cache = new ConnectorTokenCache(new TestClock());

        for (var tenant = 0; tenant < 20; tenant++)
        {
            for (var i = 0; i < 20; i++)
            {
                cache.Set(new ConnectorTokenKey($"tenant-{tenant}", Guid.NewGuid(), 1), "token", TimeSpan.FromMinutes(i + 1));
            }
        }

        cache.Count.Should().Be(ConnectorTokenCache.MaxEntries, "400 were stored, none of them over one tenant's share");
    }

    /// <summary>
    /// Over HTTP, against the raw rows and the raw response bodies rather than through the service
    /// that decrypts.
    /// </summary>
    [Fact]
    public async Task The_client_secret_is_encrypted_at_rest_and_no_response_carries_it_or_the_token()
    {
        var client = await AdminAsync();
        var provider = _scope.Provider();
        _scope.CreatedOverHttp(provider.Slug);

        var created = await client.PostAsJsonAsync("/api/connectors",
            Payload(provider, provider.TokenUrl, new() { [ConnectorSecretKeys.ClientSecret] = Secret }),
            TestContext.Current.CancellationToken);
        var createdBody = await created.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        created.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", created.StatusCode, createdBody);

        var tested = await TestAsync(client, provider.Slug);
        tested.Should().Contain("\"succeeded\":true", "the test button fetches a token and probes with it");
        provider.TokenCalls.Should().HaveCount(1);

        var one = await BodyAsync(client, $"/api/connectors/{provider.Slug}");
        var list = await BodyAsync(client, "/api/connectors");
        var stored = await StoredAsync(provider.Slug);
        var rows = await RawRowsAsync(stored.Id);

        one.Should().Contain("\"secretKeys\":[\"ClientSecret\"]", "the name says a secret is set");

        foreach (var text in new[] { createdBody, tested, one, list, rows })
        {
            text.Should().NotBeNullOrEmpty();
            text.Should().NotContain(Secret);
            text.Should().NotContain(provider.Token(1), "a token is held in memory and nowhere else");
        }

        rows.Should().Contain("ProtectedValue", "the ciphertext is what is stored in the secret's place");
    }

    /// <summary>
    /// Every logger of the host is recorded at Trace while the test button runs a grant and a
    /// probe, the HTTP client's own among them, which is where a header would be written.
    /// </summary>
    [Fact]
    public async Task Nothing_the_host_logs_during_a_grant_and_a_call_holds_the_secret_or_the_token()
    {
        var client = await AdminAsync();
        var provider = _scope.Provider();
        await CreateAsync(client, provider);

        var lines = CapturedLogs.Start();
        string tested;

        try
        {
            tested = await TestAsync(client, provider.Slug);
        }
        finally
        {
            CapturedLogs.Stop();
        }

        tested.Should().Contain("\"succeeded\":true");
        provider.TokenCalls.Should().HaveCount(1);

        var captured = lines.ToList();
        captured.Should().NotBeEmpty();
        captured.Should().Contain(l => l.Contains("ExternalApi"), "the outbound client's own loggers are among those recorded");

        var everything = string.Join('\n', captured);
        everything.Should().NotContain(Secret);
        everything.Should().NotContain(OAuthConnectors.BasicPair(provider, Secret));
        everything.Should().NotContain(provider.Token(1));
    }

    [Fact]
    public async Task An_update_that_omits_the_client_secret_keeps_it()
    {
        var client = await AdminAsync();
        var provider = _scope.Provider();
        await CreateAsync(client, provider);

        (await TestAsync(client, provider.Slug)).Should().Contain("\"succeeded\":true");

        var renamed = await PutAsync(client, provider, provider.TokenUrl, secrets: null, name: "Renamed");
        renamed.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}",
            renamed.StatusCode, await renamed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        (await TestAsync(client, provider.Slug)).Should().Contain("\"succeeded\":true");

        var tokenCalls = provider.TokenCalls;
        tokenCalls.Should().HaveCount(2, "the edit made a new version of the connector, so it asks for its own token");
        tokenCalls.Should().OnlyContain(c => c.Authorization == OAuthConnectors.BasicFor(provider, Secret),
            "the secret the update did not mention is still the one in use");
    }

    [Fact]
    public async Task Changing_the_token_url_without_the_client_secret_is_refused()
    {
        var client = await AdminAsync();
        var provider = _scope.Provider();
        var elsewhere = _scope.Provider(provider.Slug + "x");
        await CreateAsync(client, provider);

        var moved = await PutAsync(client, provider, elsewhere.TokenUrl, secrets: null);
        var body = await moved.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        moved.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "the stored secret was entered for {0}, and nobody entered it for {1}", provider.AuthHost, elsewhere.AuthHost);
        body.Should().Contain("ClientSecret").And.Contain("new or has changed");

        var otherPath = await PutAsync(client, provider, provider.TokenUrl + "/v2", secrets: null);
        otherPath.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "a token endpoint is one exact URL, and another path on the same host is another endpoint");

        (await StoredAsync(provider.Slug)).Settings[ConnectorSettingKeys.TokenUrl].Should().Be(provider.TokenUrl,
            "a refused update changes nothing");

        (await TestAsync(client, provider.Slug)).Should().Contain("\"succeeded\":true");
        provider.TokenCalls.Should().HaveCount(1, "the connector still asks its own token endpoint");
        provider.TokenCalls[0].Path.Should().Be("/token");
        elsewhere.TokenCalls.Should().BeEmpty("the stored secret must not reach the new host");

        var unchanged = await PutAsync(client, provider, provider.TokenUrl, secrets: null, name: "Renamed");
        unchanged.IsSuccessStatusCode.Should().BeTrue("the same token URL keeps the secret");

        var entered = await PutAsync(client, provider, elsewhere.TokenUrl,
            new() { [ConnectorSecretKeys.ClientSecret] = "fake-secret-entered-for-the-new-host" });
        entered.IsSuccessStatusCode.Should().BeTrue("entering the secret again is how the move is made");
    }

    [Fact]
    public async Task A_token_url_given_to_a_connector_that_held_only_the_secret_is_refused_without_it()
    {
        var client = await AdminAsync();
        var provider = _scope.Provider();
        await CreateAsync(client, provider, tokenUrl: null);

        var added = await PutAsync(client, provider, provider.TokenUrl, secrets: null);
        var body = await added.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        added.StatusCode.Should().Be(HttpStatusCode.BadRequest, "nobody entered the stored secret for this URL");
        body.Should().Contain("new or has changed", "the message covers a URL that was not there before");
        (await StoredAsync(provider.Slug)).Settings.Should().NotContainKey(ConnectorSettingKeys.TokenUrl);
    }

    [Fact]
    public async Task A_stored_token_url_that_does_not_parse_can_be_saved_again_unchanged()
    {
        var client = await AdminAsync();
        var provider = _scope.Provider();
        await CreateAsync(client, provider, tokenUrl: "not a url");

        var again = await PutAsync(client, provider, "not a url", secrets: null, name: "Renamed");

        again.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}",
            again.StatusCode, await again.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        (await StoredAsync(provider.Slug)).Name.Should().Be("Renamed");
    }

    /// <summary>
    /// The probe is held open by the stub while the connector is renamed through the update
    /// endpoint. The test endpoint read the connector before the rename.
    /// </summary>
    [Fact]
    public async Task A_test_in_flight_does_not_overwrite_an_update_made_meanwhile()
    {
        var client = await AdminAsync();
        var provider = _scope.Provider();
        await CreateAsync(client, provider);

        provider.HoldApi();
        var testing = client.PostAsync($"/api/connectors/{provider.Slug}/test", null, TestContext.Current.CancellationToken);
        await provider.ApiReached.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        var renamed = await PutAsync(client, provider, provider.TokenUrl, secrets: null, name: "Renamed during the probe");
        renamed.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}",
            renamed.StatusCode, await renamed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        provider.ReleaseApi();
        var tested = await testing;
        var testedBody = await tested.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        tested.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", tested.StatusCode, testedBody);
        testedBody.Should().Contain("\"succeeded\":true");

        var stored = await StoredAsync(provider.Slug);
        stored.Name.Should().Be("Renamed during the probe", "the update landed after the test endpoint read the connector");
        stored.LastTestedAt.Should().NotBeNull("the test still records that it ran");
        stored.LastTestResult.Should().StartWith("HTTP 200");
    }

    private IDocumentStore Store => _factory.Services.GetRequiredService<IDocumentStore>();

    private static async Task SendOkAsync(ConnectorSender sender, Connector connector, FakeProvider provider)
    {
        var result = await sender.SendAsync(
            connector, provider.Post(), SuccessRule.TwoHundredRange, null, TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue("got: {0}", result.Error);
    }

    private static object Payload(
        FakeProvider provider, string? tokenUrl, Dictionary<string, string>? secrets, string name = "Client credentials")
    {
        var settings = new Dictionary<string, string> { [ConnectorSettingKeys.ClientId] = OAuthConnectors.ClientIdOf(provider) };
        if (tokenUrl is not null) settings[ConnectorSettingKeys.TokenUrl] = tokenUrl;

        return new
        {
            name,
            slug = provider.Slug,
            baseUrl = provider.BaseUrl,
            auth = nameof(ConnectorAuth.OAuth2ClientCredentials),
            settings,
            enabled = true,
            probePath = "/",
            secrets,
        };
    }

    private async Task CreateAsync(HttpClient client, FakeProvider provider, string? tokenUrl = "")
    {
        _scope.CreatedOverHttp(provider.Slug);

        var res = await client.PostAsJsonAsync("/api/connectors",
            Payload(provider, tokenUrl == "" ? provider.TokenUrl : tokenUrl, new() { [ConnectorSecretKeys.ClientSecret] = Secret }),
            TestContext.Current.CancellationToken);

        res.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}",
            res.StatusCode, await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    private static Task<HttpResponseMessage> PutAsync(
        HttpClient client, FakeProvider provider, string? tokenUrl, Dictionary<string, string>? secrets,
        string name = "Client credentials") =>
        client.PutAsJsonAsync($"/api/connectors/{provider.Slug}", Payload(provider, tokenUrl, secrets, name),
            TestContext.Current.CancellationToken);

    private static async Task<string> TestAsync(HttpClient client, string slug)
    {
        var res = await client.PostAsync($"/api/connectors/{slug}/test", null, TestContext.Current.CancellationToken);
        var body = await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        res.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", res.StatusCode, body);
        return body;
    }

    private static async Task<string> BodyAsync(HttpClient client, string path) =>
        await (await client.GetAsync(path, TestContext.Current.CancellationToken))
            .Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

    private async Task<Connector> StoredAsync(string slug)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return await session.Query<Connector>().FirstAsync(c => c.Slug == slug, TestContext.Current.CancellationToken);
    }

    /// <summary>The connector and its secrets as Postgres holds them, not as Marten hands them back.</summary>
    private async Task<string> RawRowsAsync(Guid connectorId)
    {
        await using var conn = Store.Storage.Database.CreateConnection();
        await conn.OpenAsync(TestContext.Current.CancellationToken);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "select coalesce(string_agg(data::text, ' '), '') from ("
            + $"select data from public.mt_doc_connectors where id = '{connectorId}' "
            + $"union all select data from public.mt_doc_connector_secrets where data ->> 'ConnectorId' = '{connectorId}') as held";

        return (string)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private async Task<HttpClient> AdminAsync()
    {
        var client = Host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await _factory.StoredUserTokenAsync("SuperAdmin", "Admin"));
        return client;
    }
}
