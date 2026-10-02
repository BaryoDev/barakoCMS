using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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
/// about storage and responses go over HTTP, through the host's own sender and cache.
/// </remarks>
[Collection("Sequential")]
public class ConnectorOAuthTests
{
    private const string Secret = "cs_live_client_secret_nobody_should_see";

    private readonly IntegrationTestFixture _factory;

    public ConnectorOAuthTests(IntegrationTestFixture factory) => _factory = factory;

    private WebApplicationFactory<Program> Host => OAuthProviderStub.HostFor(_factory);

    [Fact]
    public async Task A_send_fetches_a_token_with_the_client_credentials_and_attaches_it_as_a_bearer_header()
    {
        var provider = new FakeProvider(OAuthConnectors.NewSlug());
        var tenant = OAuthConnectors.NewTenant();
        var connector = await OAuthConnectors.SeedAsync(Host.Services, tenant, provider, Secret, settings: new()
        {
            [ConnectorSettingKeys.TokenUrl] = provider.TokenUrl,
            [ConnectorSettingKeys.ClientId] = OAuthConnectors.ClientIdOf(provider),
            [ConnectorSettingKeys.Scope] = "accounting.read",
            [ConnectorSettingKeys.Audience] = "https://api.example",
        });
        var log = new CapturingLogger();

        await using var session = Store.QuerySession(tenant);
        var sender = OAuthConnectors.Sender(Host.Services, session, new ConnectorTokenCache(new TestClock()), log);

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

        string.Join('\n', log.Lines).Should().NotContain(Secret).And.NotContain(provider.Token(1));
    }

    [Fact]
    public async Task Client_credentials_go_in_the_form_body_when_the_connector_asks_for_it()
    {
        var provider = new FakeProvider(OAuthConnectors.NewSlug());
        var tenant = OAuthConnectors.NewTenant();
        var connector = await OAuthConnectors.SeedAsync(Host.Services, tenant, provider, Secret, settings: new()
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
        var provider = new FakeProvider(OAuthConnectors.NewSlug());
        var tenant = OAuthConnectors.NewTenant();
        var connector = await OAuthConnectors.SeedAsync(Host.Services, tenant, provider, Secret);

        await using var session = Store.QuerySession(tenant);
        var sender = OAuthConnectors.Sender(Host.Services, session, new ConnectorTokenCache(new TestClock()));

        for (var i = 0; i < 2; i++)
        {
            var result = await sender.SendAsync(
                connector, provider.Post(), SuccessRule.TwoHundredRange, null, TestContext.Current.CancellationToken);
            result.Succeeded.Should().BeTrue("got: {0}", result.Error);
        }

        provider.TokenCalls.Should().HaveCount(1, "the second send is inside the hour the token was granted for");

        var apiCalls = provider.ApiCalls;
        apiCalls.Should().HaveCount(2);
        apiCalls.Should().OnlyContain(c => c.Authorization == $"Bearer {provider.Token(1)}");
    }

    [Fact]
    public async Task An_expired_token_is_fetched_again_shortly_before_its_lifetime_ends()
    {
        var provider = new FakeProvider(OAuthConnectors.NewSlug()) { Expiry = ",\"expires_in\":600" };
        var tenant = OAuthConnectors.NewTenant();
        var connector = await OAuthConnectors.SeedAsync(Host.Services, tenant, provider, Secret);
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
    public async Task A_missing_or_unusable_lifetime_gets_a_short_one_and_a_long_one_is_capped(
        string expiry, int stillCachedAt, int fetchedAgainAt)
    {
        var provider = new FakeProvider(OAuthConnectors.NewSlug()) { Expiry = expiry };
        var tenant = OAuthConnectors.NewTenant();
        var connector = await OAuthConnectors.SeedAsync(Host.Services, tenant, provider, Secret);
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
        var provider = new FakeProvider(OAuthConnectors.NewSlug());
        var tenant = OAuthConnectors.NewTenant();
        var cache = new ConnectorTokenCache(new TestClock());
        var before = await OAuthConnectors.SeedAsync(Host.Services, tenant, provider, Secret);

        await using (var session = Store.QuerySession(tenant))
        {
            await SendOkAsync(OAuthConnectors.Sender(Host.Services, session, cache), before, provider);
        }

        await using (var edit = Store.LightweightSession(tenant))
        {
            var stored = await edit.Query<ConnectorSecret>()
                .FirstAsync(s => s.ConnectorId == before.Id, TestContext.Current.CancellationToken);
            stored.ProtectedValue = Host.Services.GetRequiredService<IConnectorSecretProtector>().Protect("cs_rotated_secret");
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
        tokenCalls[1].Authorization.Should().Be(OAuthConnectors.BasicFor(provider, "cs_rotated_secret"));
        cache.Count.Should().Be(1, "the token for the old version is dropped when the new one is stored");
    }

    /// <summary>
    /// Both tenants hold a connector with the same id and the same version, so the tenant is the
    /// only part of the cache key that tells them apart.
    /// </summary>
    [Fact]
    public async Task Another_tenants_connector_does_not_share_a_cached_token()
    {
        var provider = new FakeProvider(OAuthConnectors.NewSlug());
        var cache = new ConnectorTokenCache(new TestClock());
        var id = Guid.NewGuid();
        var version = DateTime.UtcNow;
        var first = OAuthConnectors.NewTenant();
        var second = OAuthConnectors.NewTenant();

        var mine = await OAuthConnectors.SeedAsync(Host.Services, first, provider, Secret, id, version);
        var theirs = await OAuthConnectors.SeedAsync(Host.Services, second, provider, "cs_the_other_tenants_secret", id, version);

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
        tokenCalls[1].Authorization.Should().Be(OAuthConnectors.BasicFor(provider, "cs_the_other_tenants_secret"));

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
    public async Task A_token_endpoint_that_does_not_grant_a_token_is_named_and_its_body_is_not_repeated(
        int status, string body, string expected)
    {
        var provider = new FakeProvider(OAuthConnectors.NewSlug());
        provider.TokenAnswer = _ => OAuthProviderStub.Json((HttpStatusCode)status, body.Replace("ECHO", Secret));
        var tenant = OAuthConnectors.NewTenant();
        var connector = await OAuthConnectors.SeedAsync(Host.Services, tenant, provider, Secret);
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
        var provider = new FakeProvider(OAuthConnectors.NewSlug());
        var tenant = OAuthConnectors.NewTenant();
        var settings = new Dictionary<string, string> { [ConnectorSettingKeys.ClientId] = clientId };
        if (tokenUrl is not null) settings[ConnectorSettingKeys.TokenUrl] = tokenUrl;
        var connector = await OAuthConnectors.SeedAsync(Host.Services, tenant, provider, Secret, settings: settings);

        await using var session = Store.QuerySession(tenant);
        var sender = OAuthConnectors.Sender(Host.Services, session, new ConnectorTokenCache(new TestClock()));

        var result = await sender.SendAsync(
            connector, provider.Post(), SuccessRule.TwoHundredRange, null, TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain($"'{named}'");
        provider.ApiCalls.Should().BeEmpty();
    }

    /// <summary>
    /// The token URL is dialled through the same guarded client as every other outbound call. This
    /// one uses the fixture's own host, where that client's handler is the real one.
    /// </summary>
    [Fact]
    public async Task A_token_url_on_a_blocked_address_is_refused()
    {
        var provider = new FakeProvider(OAuthConnectors.NewSlug());
        var tenant = OAuthConnectors.NewTenant();
        var connector = await OAuthConnectors.SeedAsync(_factory.Services, tenant, provider, Secret, settings: new()
        {
            [ConnectorSettingKeys.TokenUrl] = "http://169.254.169.254/token",
            [ConnectorSettingKeys.ClientId] = OAuthConnectors.ClientIdOf(provider),
        });

        await using var session = Store.QuerySession(tenant);
        var sender = OAuthConnectors.Sender(_factory.Services, session, new ConnectorTokenCache(new TestClock()));

        var result = await sender.SendAsync(
            connector, provider.Post(), SuccessRule.TwoHundredRange, null, TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.StatusCode.Should().BeNull();
        result.Error.Should().Contain("169.254.169.254", "the refusal names the token endpoint's host");
        result.Error.Should().NotContain("answered",
            "where the metadata address exists it answers when dialled, so an answer of any kind means the guard was not in the way");
    }

    [Fact]
    public void The_cache_holds_a_bounded_number_of_tokens()
    {
        var cache = new ConnectorTokenCache(new TestClock());

        for (var i = 0; i < ConnectorTokenCache.MaxEntries + 50; i++)
        {
            cache.Set(new ConnectorTokenKey("tenant", Guid.NewGuid(), 1), $"token-{i}", TimeSpan.FromMinutes(i + 1));
        }

        cache.Count.Should().Be(ConnectorTokenCache.MaxEntries);
    }

    /// <summary>
    /// Over HTTP, against the raw rows and the raw response bodies rather than through the service
    /// that decrypts.
    /// </summary>
    [Fact]
    public async Task The_client_secret_is_encrypted_at_rest_and_no_response_carries_it_or_the_token()
    {
        var client = await AdminAsync();
        var provider = new FakeProvider(OAuthConnectors.NewSlug());

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

    [Fact]
    public async Task An_update_that_omits_the_client_secret_keeps_it()
    {
        var client = await AdminAsync();
        var provider = new FakeProvider(OAuthConnectors.NewSlug());
        await CreateAsync(client, provider);

        (await TestAsync(client, provider.Slug)).Should().Contain("\"succeeded\":true");

        var renamed = await client.PutAsJsonAsync($"/api/connectors/{provider.Slug}",
            Payload(provider, provider.TokenUrl, secrets: null, name: "Renamed"), TestContext.Current.CancellationToken);
        renamed.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}",
            renamed.StatusCode, await renamed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        (await TestAsync(client, provider.Slug)).Should().Contain("\"succeeded\":true");

        var tokenCalls = provider.TokenCalls;
        tokenCalls.Should().HaveCount(2, "the edit made a new version of the connector, so it asks for its own token");
        tokenCalls.Should().OnlyContain(c => c.Authorization == OAuthConnectors.BasicFor(provider, Secret),
            "the secret the update did not mention is still the one in use");
    }

    [Fact]
    public async Task Moving_the_token_url_to_another_host_without_the_client_secret_is_refused()
    {
        var client = await AdminAsync();
        var provider = new FakeProvider(OAuthConnectors.NewSlug());
        var elsewhere = new FakeProvider(provider.Slug + "x");
        await CreateAsync(client, provider);

        var moved = await client.PutAsJsonAsync($"/api/connectors/{provider.Slug}",
            Payload(provider, elsewhere.TokenUrl, secrets: null), TestContext.Current.CancellationToken);
        var body = await moved.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        moved.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "the stored secret was entered for {0}, and nobody entered it for {1}", provider.AuthHost, elsewhere.AuthHost);
        body.Should().Contain("ClientSecret");

        (await StoredAsync(provider.Slug)).Settings[ConnectorSettingKeys.TokenUrl].Should().Be(provider.TokenUrl,
            "a refused update changes nothing");

        (await TestAsync(client, provider.Slug)).Should().Contain("\"succeeded\":true");
        provider.TokenCalls.Should().HaveCount(1, "the connector still asks its own token endpoint");
        elsewhere.TokenCalls.Should().BeEmpty("the stored secret must not reach the new host");

        var samePlace = await client.PutAsJsonAsync($"/api/connectors/{provider.Slug}",
            Payload(provider, provider.TokenUrl + "/v2", secrets: null), TestContext.Current.CancellationToken);
        samePlace.IsSuccessStatusCode.Should().BeTrue("a path change on the same origin keeps the secret");

        var entered = await client.PutAsJsonAsync($"/api/connectors/{provider.Slug}",
            Payload(provider, elsewhere.TokenUrl, new() { [ConnectorSecretKeys.ClientSecret] = "cs_entered_for_the_new_host" }),
            TestContext.Current.CancellationToken);
        entered.IsSuccessStatusCode.Should().BeTrue("entering the secret again is how the move is made");
    }

    private IDocumentStore Store => _factory.Services.GetRequiredService<IDocumentStore>();

    private static async Task SendOkAsync(ConnectorSender sender, Connector connector, FakeProvider provider)
    {
        var result = await sender.SendAsync(
            connector, provider.Post(), SuccessRule.TwoHundredRange, null, TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue("got: {0}", result.Error);
    }

    private static object Payload(
        FakeProvider provider, string tokenUrl, Dictionary<string, string>? secrets, string name = "Client credentials") => new
    {
        name,
        slug = provider.Slug,
        baseUrl = provider.BaseUrl,
        auth = nameof(ConnectorAuth.OAuth2ClientCredentials),
        settings = new Dictionary<string, string>
        {
            [ConnectorSettingKeys.TokenUrl] = tokenUrl,
            [ConnectorSettingKeys.ClientId] = OAuthConnectors.ClientIdOf(provider),
        },
        enabled = true,
        probePath = "/",
        secrets,
    };

    private static async Task CreateAsync(HttpClient client, FakeProvider provider)
    {
        var res = await client.PostAsJsonAsync("/api/connectors",
            Payload(provider, provider.TokenUrl, new() { [ConnectorSecretKeys.ClientSecret] = Secret }),
            TestContext.Current.CancellationToken);

        res.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}",
            res.StatusCode, await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

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
