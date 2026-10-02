using barakoCMS.Infrastructure.Connectors;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Features.Connectors;

/// <summary>
/// A provider that answers 401 to a cached token gets one new token and one more send.
/// </summary>
/// <remarks>
/// A provider can revoke a token inside the lifetime it gave, and the cache cannot know. Each of
/// the three ways out (a send, the test button's probe, a collection sync's fetch) is driven here,
/// with the token calls and the API calls both counted, since "once" is the claim. The clock is
/// moved two minutes on before each refusal, because a token granted less than a minute ago is
/// not replaced.
/// </remarks>
[Collection("Sequential")]
public class ConnectorOAuthRetryTests : IAsyncLifetime
{
    private const string Secret = "fake-client-secret-for-the-retry-tests";

    private readonly IntegrationTestFixture _factory;
    private readonly OAuthTestScope _scope;
    private readonly TestClock _clock = new();

    public ConnectorOAuthRetryTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _scope = new OAuthTestScope(factory);
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _scope.DisposeAsync();

    private WebApplicationFactory<Program> Host => OAuthProviderStub.HostFor(_factory);

    [Fact]
    public async Task A_send_refused_with_a_cached_token_fetches_one_new_token_and_goes_once_more()
    {
        var (provider, connector, session) = await ArrangeAsync();
        await using var held = session;
        var sender = OAuthConnectors.Sender(Host.Services, session, new ConnectorTokenCache(_clock));

        var warm = await SendAsync(sender, connector, provider);
        warm.Succeeded.Should().BeTrue("got: {0}", warm.Error);

        provider.Revoke(1);
        _clock.Now = _clock.Now.AddMinutes(2);

        var result = await SendAsync(sender, connector, provider);

        result.Succeeded.Should().BeTrue("the second token is a good one; got: {0}", result.Error);
        result.StatusCode.Should().Be(200);
        provider.TokenCalls.Should().HaveCount(2);

        var apiCalls = provider.ApiCalls;
        apiCalls.Should().HaveCount(3, "one warm send, the refused send, and its one repeat");
        apiCalls[1].Authorization.Should().Be($"Bearer {provider.Token(1)}");
        apiCalls[2].Authorization.Should().Be($"Bearer {provider.Token(2)}");
        apiCalls[2].Body.Should().Be(apiCalls[1].Body, "the repeat carries the same composed request");
    }

    [Fact]
    public async Task A_send_still_refused_after_the_new_token_is_reported_and_not_sent_a_third_time()
    {
        var (provider, connector, session) = await ArrangeAsync();
        await using var held = session;
        var sender = OAuthConnectors.Sender(Host.Services, session, new ConnectorTokenCache(_clock));

        var warm = await SendAsync(sender, connector, provider);
        warm.Succeeded.Should().BeTrue("got: {0}", warm.Error);

        provider.RefuseEverything = true;
        _clock.Now = _clock.Now.AddMinutes(2);

        var result = await SendAsync(sender, connector, provider);

        result.Succeeded.Should().BeFalse();
        result.StatusCode.Should().Be(401);
        provider.TokenCalls.Should().HaveCount(2, "one new token, no more");
        provider.ApiCalls.Should().HaveCount(3, "one warm send, the refused send, and one repeat, no more");
    }

    /// <summary>
    /// A provider that answers 401 for a reason the token cannot fix. The first refusal buys one
    /// new token; the refusal right after it finds that token under a minute old and buys nothing.
    /// </summary>
    [Fact]
    public async Task Two_refused_sends_in_a_row_make_one_extra_token_request_not_two()
    {
        var (provider, connector, session) = await ArrangeAsync();
        await using var held = session;
        var sender = OAuthConnectors.Sender(Host.Services, session, new ConnectorTokenCache(_clock));

        var warm = await SendAsync(sender, connector, provider);
        warm.Succeeded.Should().BeTrue("got: {0}", warm.Error);

        provider.RefuseEverything = true;
        _clock.Now = _clock.Now.AddMinutes(2);

        (await SendAsync(sender, connector, provider)).StatusCode.Should().Be(401);
        provider.TokenCalls.Should().HaveCount(2);
        provider.ApiCalls.Should().HaveCount(3);

        _clock.Now = _clock.Now.AddSeconds(59);

        (await SendAsync(sender, connector, provider)).StatusCode.Should().Be(401);
        provider.TokenCalls.Should().HaveCount(2, "the token in hand was granted 59 seconds ago, so a new one would be refused too");
        provider.ApiCalls.Should().HaveCount(4, "one send, not repeated");

        _clock.Now = _clock.Now.AddSeconds(2);

        (await SendAsync(sender, connector, provider)).StatusCode.Should().Be(401);
        provider.TokenCalls.Should().HaveCount(3, "past a minute the token may have been revoked, so it is worth one more");
        provider.ApiCalls.Should().HaveCount(6);
    }

    /// <summary>
    /// A guard: this passes with or without the repeat. A token granted for this very call was not
    /// stale, so a 401 to it is the provider's answer and asking again would get the same one.
    /// </summary>
    [Fact]
    public async Task A_send_refused_with_a_token_fetched_for_it_is_not_repeated()
    {
        var (provider, connector, session) = await ArrangeAsync();
        await using var held = session;
        var sender = OAuthConnectors.Sender(Host.Services, session, new ConnectorTokenCache(_clock));

        provider.RefuseEverything = true;

        var result = await SendAsync(sender, connector, provider);

        result.Succeeded.Should().BeFalse();
        result.StatusCode.Should().Be(401);
        provider.TokenCalls.Should().HaveCount(1);
        provider.ApiCalls.Should().HaveCount(1);
    }

    [Fact]
    public async Task A_probe_refused_with_a_cached_token_fetches_one_new_token_and_goes_once_more()
    {
        var (provider, connector, session) = await ArrangeAsync();
        await using var held = session;
        var sender = OAuthConnectors.Sender(Host.Services, session, new ConnectorTokenCache(_clock));

        var warm = await sender.ProbeAsync(connector, TestContext.Current.CancellationToken);
        warm.Succeeded.Should().BeTrue("got: {0}", warm.Error);

        provider.Revoke(1);
        _clock.Now = _clock.Now.AddMinutes(2);

        var result = await sender.ProbeAsync(connector, TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue("got: {0}", result.Error);
        result.StatusCode.Should().Be(200);
        provider.TokenCalls.Should().HaveCount(2);
        provider.ApiCalls.Should().HaveCount(3);
    }

    [Fact]
    public async Task A_fetch_refused_with_a_cached_token_fetches_one_new_token_and_goes_once_more()
    {
        var (provider, connector, session) = await ArrangeAsync();
        await using var held = session;
        var sender = OAuthConnectors.Sender(Host.Services, session, new ConnectorTokenCache(_clock));

        var warm = await sender.FetchAsync(connector, provider.Get(), 1024, TestContext.Current.CancellationToken);
        warm.Succeeded.Should().BeTrue("got: {0}", warm.Error);

        provider.Revoke(1);
        _clock.Now = _clock.Now.AddMinutes(2);

        var result = await sender.FetchAsync(connector, provider.Get(), 1024, TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue("got: {0}", result.Error);
        result.Body.Should().Be(FakeProvider.ApiBody, "the body read is the repeat's, not the 401's");
        provider.TokenCalls.Should().HaveCount(2);
        provider.ApiCalls.Should().HaveCount(3);
    }

    private async Task<(FakeProvider Provider, Connector Connector, IQuerySession Session)> ArrangeAsync()
    {
        var provider = _scope.Provider();
        var tenant = OAuthConnectors.NewTenant();
        var connector = await _scope.SeedAsync(tenant, provider, Secret);

        return (provider, connector, _factory.Services.GetRequiredService<IDocumentStore>().QuerySession(tenant));
    }

    private static Task<ConnectorCallResult> SendAsync(ConnectorSender sender, Connector connector, FakeProvider provider) =>
        sender.SendAsync(connector, provider.Post(), SuccessRule.TwoHundredRange, null, TestContext.Current.CancellationToken);
}
