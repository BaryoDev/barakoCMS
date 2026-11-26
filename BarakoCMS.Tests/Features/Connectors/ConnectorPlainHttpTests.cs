using barakoCMS.Infrastructure.Connectors;
using barakoCMS.Infrastructure.Http;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Features.Connectors;

/// <summary>
/// A connector's credentials over plain http: refused to a host on another network, allowed to
/// one the operator listed in Webhooks:AllowedHosts.
/// </summary>
/// <remarks>
/// The stub answers by host whatever the scheme, so without the check each refused call here
/// would reach it and be counted. Loopback over http is covered by the OAuth test whose token
/// endpoint is 127.0.0.1: it gets as far as the address guard.
/// </remarks>
[Collection("Integration1")]
public class ConnectorPlainHttpTests : IAsyncLifetime
{
    private const string Secret = "fake-client-secret-for-the-plain-http-tests";
    private const string Refusal = "Use an https URL.";

    private readonly IntegrationTestFixture _factory;
    private readonly OAuthTestScope _scope;
    private readonly List<(string Tenant, Guid Id)> _bearers = [];

    public ConnectorPlainHttpTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _scope = new OAuthTestScope(factory);
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var (tenant, id) in _bearers)
        {
            await using var session = Store.LightweightSession(tenant);
            session.DeleteWhere<ConnectorSecret>(s => s.ConnectorId == id);
            session.Delete<Connector>(id);
            await session.SaveChangesAsync();
        }

        await _scope.DisposeAsync();
    }

    private WebApplicationFactory<Program> Host => OAuthProviderStub.HostFor(_factory);

    private IDocumentStore Store => _factory.Services.GetRequiredService<IDocumentStore>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_bearer_token_is_not_sent_over_plain_http_to_a_host_nobody_listed()
    {
        var provider = _scope.Provider();
        var tenant = OAuthConnectors.NewTenant();
        var connector = await SeedBearerAsync(tenant, $"http://{provider.ApiHost}");

        await using var session = Store.QuerySession(tenant);
        var sender = OAuthConnectors.Sender(Host.Services, session, new ConnectorTokenCache(new TestClock()));

        var result = await sender.SendAsync(connector, PlainPost(provider), SuccessRule.TwoHundredRange, null, Ct);

        result.Succeeded.Should().BeFalse();
        result.StatusCode.Should().BeNull();
        result.Error.Should().Contain(Refusal);
        provider.ApiCalls.Should().BeEmpty("the token would cross the network in clear text");
    }

    [Fact]
    public async Task An_oauth_connector_on_plain_http_asks_for_no_token_and_sends_nothing()
    {
        var provider = _scope.Provider();
        var tenant = OAuthConnectors.NewTenant();
        var connector = await _scope.SeedAsync(tenant, provider, Secret, baseUrl: $"http://{provider.ApiHost}");

        await using var session = Store.QuerySession(tenant);
        var sender = OAuthConnectors.Sender(Host.Services, session, new ConnectorTokenCache(new TestClock()));

        var result = await sender.SendAsync(connector, PlainPost(provider), SuccessRule.TwoHundredRange, null, Ct);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain(Refusal);
        provider.TokenCalls.Should().BeEmpty("a token fetched for a call that cannot go is a client secret sent for nothing");
        provider.ApiCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_token_url_on_plain_http_is_refused_before_the_client_secret_goes()
    {
        var provider = _scope.Provider();
        var tenant = OAuthConnectors.NewTenant();
        var connector = await _scope.SeedAsync(tenant, provider, Secret, settings: new()
        {
            [ConnectorSettingKeys.TokenUrl] = $"http://{provider.AuthHost}/token",
            [ConnectorSettingKeys.ClientId] = OAuthConnectors.ClientIdOf(provider),
        });

        await using var session = Store.QuerySession(tenant);
        var sender = OAuthConnectors.Sender(Host.Services, session, new ConnectorTokenCache(new TestClock()));

        var result = await sender.SendAsync(connector, provider.Post(), SuccessRule.TwoHundredRange, null, Ct);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain(Refusal);
        provider.TokenCalls.Should().BeEmpty("the token request carries the client secret in its Basic header");
        provider.ApiCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task Testing_a_connector_on_plain_http_says_to_use_https()
    {
        var provider = _scope.Provider();
        var tenant = OAuthConnectors.NewTenant();
        var connector = await SeedBearerAsync(tenant, $"http://{provider.ApiHost}");

        await using var session = Store.QuerySession(tenant);
        var sender = OAuthConnectors.Sender(Host.Services, session, new ConnectorTokenCache(new TestClock()));

        var result = await sender.ProbeAsync(connector, Ct);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain(Refusal);
        provider.ApiCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_fetch_over_plain_http_sends_nothing()
    {
        var provider = _scope.Provider();
        var tenant = OAuthConnectors.NewTenant();
        var connector = await SeedBearerAsync(tenant, $"http://{provider.ApiHost}");

        await using var session = Store.QuerySession(tenant);
        var sender = OAuthConnectors.Sender(Host.Services, session, new ConnectorTokenCache(new TestClock()));

        var result = await sender.FetchAsync(
            connector, new ComposedRequest("GET", $"http://{provider.ApiHost}/things", new(), null, null), 64 * 1024, Ct);

        result.Succeeded.Should().BeFalse();
        provider.ApiCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_connector_with_no_auth_still_uses_plain_http()
    {
        var provider = _scope.Provider();
        var connector = new Connector
        {
            Id = Guid.NewGuid(),
            Name = "Open",
            Slug = provider.Slug,
            BaseUrl = $"http://{provider.ApiHost}",
            Auth = ConnectorAuth.None,
        };

        await using var session = Store.QuerySession(OAuthConnectors.NewTenant());
        var sender = OAuthConnectors.Sender(Host.Services, session, new ConnectorTokenCache(new TestClock()));

        await sender.SendAsync(connector, PlainPost(provider), SuccessRule.TwoHundredRange, null, Ct);

        provider.ApiCalls.Should().ContainSingle().Which.Authorization.Should().BeNull();
    }

    [Fact]
    public async Task A_host_listed_in_AllowedHosts_takes_credentials_over_plain_http()
    {
        var provider = _scope.Provider();
        var tenant = OAuthConnectors.NewTenant();
        var connector = await _scope.SeedAsync(tenant, provider, Secret, baseUrl: $"http://{provider.ApiHost}", settings: new()
        {
            [ConnectorSettingKeys.TokenUrl] = $"http://{provider.AuthHost}/token",
            [ConnectorSettingKeys.ClientId] = OAuthConnectors.ClientIdOf(provider),
        });

        // Upper case on purpose: a host name matches whatever its case.
        var guard = new OutboundAddressGuard(allowedHosts: [provider.ApiHost.ToUpperInvariant(), $"{provider.AuthHost}:80"]);

        await using var session = Store.QuerySession(tenant);
        var sender = OAuthConnectors.Sender(Host.Services, session, new ConnectorTokenCache(new TestClock()), guard: guard);

        var result = await sender.SendAsync(connector, PlainPost(provider), SuccessRule.TwoHundredRange, null, Ct);

        result.Succeeded.Should().BeTrue("got: {0}", result.Error);
        provider.TokenCalls.Should().ContainSingle();
        provider.ApiCalls.Should().ContainSingle().Which.Authorization.Should().Be($"Bearer {provider.Token(1)}");
    }

    [Fact]
    public async Task A_listed_host_on_another_port_is_still_refused_over_plain_http()
    {
        var provider = _scope.Provider();
        var tenant = OAuthConnectors.NewTenant();
        var connector = await SeedBearerAsync(tenant, $"http://{provider.ApiHost}:8080");

        // A name with no port allows 80 and 443 only, the same as the address guard.
        var guard = new OutboundAddressGuard(allowedHosts: [provider.ApiHost]);

        await using var session = Store.QuerySession(tenant);
        var sender = OAuthConnectors.Sender(Host.Services, session, new ConnectorTokenCache(new TestClock()), guard: guard);

        var result = await sender.SendAsync(
            connector, new ComposedRequest("POST", $"http://{provider.ApiHost}:8080/things", new(), "{\"n\":1}", "application/json"),
            SuccessRule.TwoHundredRange, null, Ct);

        result.Error.Should().Contain(Refusal);
        provider.ApiCalls.Should().BeEmpty();
    }

    private static ComposedRequest PlainPost(FakeProvider provider) =>
        new("POST", $"http://{provider.ApiHost}/things", new(), "{\"n\":1}", "application/json");

    private async Task<Connector> SeedBearerAsync(string tenant, string baseUrl)
    {
        var connector = new Connector
        {
            Id = Guid.NewGuid(),
            Name = "Bearer",
            Slug = "plain" + Guid.NewGuid().ToString("n")[..10],
            BaseUrl = baseUrl,
            Auth = ConnectorAuth.BearerToken,
            SecretKeys = [ConnectorSecretKeys.Token],
        };

        var protector = _factory.Services.GetRequiredService<IConnectorSecretProtector>();

        await using var session = Store.LightweightSession(tenant);
        session.Store(connector);
        session.Store(new ConnectorSecret
        {
            Id = Guid.NewGuid(),
            ConnectorId = connector.Id,
            Key = ConnectorSecretKeys.Token,
            ProtectedValue = protector.Protect("fake-bearer-for-the-plain-http-tests"),
        });
        await session.SaveChangesAsync(Ct);

        _bearers.Add((tenant, connector.Id));
        return connector;
    }
}
