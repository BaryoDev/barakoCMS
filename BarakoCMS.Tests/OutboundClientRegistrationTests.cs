using barakoCMS.Infrastructure.Http;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// The registered ExternalApi client, resolved from the test host: the one client a tenant or an
/// admin can aim. The source scans live in <see cref="OutboundClientRegistrationScanTests"/>.
/// </summary>
[Collection("Integration1")]
public class OutboundClientRegistrationTests
{
    private readonly IntegrationTestFixture _factory;

    public OutboundClientRegistrationTests(IntegrationTestFixture factory) => _factory = factory;

    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://127.0.0.1:9/")]
    public async Task ExternalApiClient_RefusesMetadataTarget(string target)
    {
        var client = _factory.Services.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientNames.ExternalApi);
        client.Timeout.Should().BeGreaterThan(TimeSpan.Zero).And.NotBe(Timeout.InfiniteTimeSpan);

        var act = () => client.GetAsync(target, TestContext.Current.CancellationToken);

        var thrown = (await act.Should().ThrowAsync<HttpRequestException>()).Which;
        Chain(thrown).Should().Contain(e => e is BlockedAddressException, "the guard refused the address before any connection");
    }

    [Fact]
    public void ExternalApiClient_IsGuardedAndDoesNotFollowRedirects()
    {
        var handler = _factory.Services.GetRequiredService<IHttpMessageHandlerFactory>()
            .CreateHandler(HttpClientNames.ExternalApi);
        while (handler is DelegatingHandler delegating)
        {
            handler = delegating.InnerHandler!;
        }

        var primary = handler.Should().BeOfType<SocketsHttpHandler>().Which;
        primary.AllowAutoRedirect.Should().BeFalse();
        primary.ConnectCallback.Should().NotBeNull("every connection is dialled through the guard");
    }

    private static IEnumerable<Exception> Chain(Exception e)
    {
        for (var x = e; x is not null; x = x.InnerException!)
        {
            yield return x;
        }
    }
}
