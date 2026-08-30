using System.Net.Http.Json;
using barakoCMS.Infrastructure.Http;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// The primary handler behind the webhook client refuses an internal address and does not follow
/// redirects, so the SSRF guard covers more than the URL a workflow author typed.
/// </summary>
/// <remarks>
/// <c>WebhookAction</c> used to validate the URL it was given and then hand it to a client whose
/// <c>AllowAutoRedirect</c> was left at its default of true. A webhook target answering
/// <c>302 Location: http://169.254.169.254/latest/meta-data/...</c> was followed to the metadata
/// service with the block list never consulted for that address.
///
/// The assertions are on the client from the application's own DI container and on the handler its
/// registration builds, because both defects were in that registration rather than in any code path
/// that could be tested in isolation.
///
/// The two tests are a pair. The first shows the real registration refuses a blocked address; the
/// second shows that a handler built the same way, differing only in an address policy that permits
/// loopback, does connect and does not follow the redirect. Without the second, a guard that refused
/// every address would pass the first.
/// </remarks>
[Collection("Sequential")]
public class WebhookRedirectTests
{
    private readonly IntegrationTestFixture _fixture;

    public WebhookRedirectTests(IntegrationTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task The_registered_webhook_client_refuses_an_internal_address()
    {
        using var target = new RecordingListener();

        using var scope = _fixture.Services.CreateScope();
        var client = scope.ServiceProvider
            .GetRequiredService<IHttpClientFactory>()
            .CreateClient("ExternalApi");

        var post = async () => await client.PostAsJsonAsync(target.Url, new { probe = true });

        await post.Should().ThrowAsync<HttpRequestException>();
        target.WasCalled.Should().BeFalse(
            "the connect callback checks the address it is about to dial, so no socket is opened to loopback");
    }

    [Fact]
    public async Task The_webhook_client_does_not_follow_a_redirect()
    {
        using var target = new RecordingListener();
        using var redirector = new RecordingListener(redirectTo: target.Url);

        using var handler = OutboundHttpHandler.Create(PermitsLoopback);
        using var client = new HttpClient(handler);

        var response = await client.PostAsJsonAsync(redirector.Url, new { probe = true });

        redirector.WasCalled.Should().BeTrue("the first hop is the one the URL guard validated");
        target.WasCalled.Should().BeFalse(
            "following the redirect is what let a webhook reach the metadata service, which no URL "
            + "guard on the original address can prevent");
        ((int)response.StatusCode).Should().Be(302,
            "the redirect comes back to the caller instead of being followed");
    }

    private static OutboundAddressGuard PermitsLoopback => new(isBlocked: _ => false);
}
