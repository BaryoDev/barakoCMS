using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// The webhook client does not follow redirects, so the SSRF guard covers more than the first hop.
/// </summary>
/// <remarks>
/// <c>WebhookAction</c> validates the URL it is given and then hands it to a client whose
/// <c>AllowAutoRedirect</c> was left at its default of true. A webhook target answering
/// <c>302 Location: http://169.254.169.254/latest/meta-data/...</c> was followed to the metadata
/// service with <c>IsBlockedAddress</c> never consulted for that address.
///
/// The redirect here goes to a second loopback listener rather than to a real link-local address.
/// Pointing a test at 169.254.169.254 either hangs for its timeout or, on a cloud runner, reaches
/// something real. The mechanism under test is whether the handler follows a redirect at all, and a
/// second listener answers that precisely: it records whether it was ever contacted.
///
/// The assertion is on the named client from the application's own DI container, because the defect
/// was in that registration rather than in any code path that could be tested in isolation.
/// </remarks>
[Collection("Sequential")]
public class WebhookRedirectTests
{
    private readonly IntegrationTestFixture _fixture;

    public WebhookRedirectTests(IntegrationTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task The_webhook_client_does_not_follow_a_redirect()
    {
        using var target = new RecordingListener();
        using var redirector = new RecordingListener(redirectTo: target.Url);

        using var scope = _fixture.Services.CreateScope();
        var client = scope.ServiceProvider
            .GetRequiredService<IHttpClientFactory>()
            .CreateClient("ExternalApi");

        var response = await client.PostAsJsonAsync(redirector.Url, new { probe = true });

        redirector.WasCalled.Should().BeTrue("the first hop is the one the URL guard validated");
        target.WasCalled.Should().BeFalse(
            "following the redirect is what let a webhook reach the metadata service, which no URL "
            + "guard on the original address can prevent");
        ((int)response.StatusCode).Should().Be(302,
            "the redirect comes back to the caller instead of being followed");
    }

    /// <summary>
    /// A loopback listener that records whether anything reached it, and optionally answers 302.
    /// </summary>
    private sealed class RecordingListener : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stopping = new();

        public RecordingListener(string? redirectTo = null)
        {
            var port = FreePort();
            Url = $"http://127.0.0.1:{port}/";
            _listener.Prefixes.Add(Url);
            _listener.Start();
            _ = ServeAsync(redirectTo);
        }

        public string Url { get; }

        public bool WasCalled { get; private set; }

        private async Task ServeAsync(string? redirectTo)
        {
            while (!_stopping.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch
                {
                    return;
                }

                WasCalled = true;
                if (redirectTo is not null)
                {
                    context.Response.StatusCode = 302;
                    context.Response.Headers["Location"] = redirectTo;
                }
                else
                {
                    context.Response.StatusCode = 200;
                }

                context.Response.Close();
            }
        }

        private static int FreePort()
        {
            using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        public void Dispose()
        {
            _stopping.Cancel();
            _listener.Close();
            _stopping.Dispose();
        }
    }
}
