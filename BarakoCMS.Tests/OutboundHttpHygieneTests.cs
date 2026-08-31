using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using BarakoCMS.AI;
using barakoCMS.Features.Workflows.Actions;
using barakoCMS.Infrastructure.Http;
using barakoCMS.Models;

namespace BarakoCMS.Tests;

/// <summary>Records whether the response body it was attached to was ever disposed.</summary>
internal sealed class DisposalRecordingContent : HttpContent
{
    public bool Disposed { get; private set; }

    protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
        stream.WriteAsync(new byte[] { (byte)'o', (byte)'k' }, 0, 2);

    protected override bool TryComputeLength(out long length)
    {
        length = 2;
        return true;
    }

    protected override void Dispose(bool disposing)
    {
        Disposed = true;
        base.Dispose(disposing);
    }
}

internal sealed class StubHandler : HttpMessageHandler
{
    private readonly HttpResponseMessage _response;

    public StubHandler(HttpResponseMessage response) => _response = response;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        Task.FromResult(_response);
}

internal sealed class StubHttpClientFactory : IHttpClientFactory
{
    private readonly HttpClient _client;

    public StubHttpClientFactory(HttpClient client) => _client = client;

    public HttpClient CreateClient(string name) => _client;
}

/// <summary>
/// Two outbound HTTP calls that handled their own results badly.
/// </summary>
/// <remarks>
/// <c>WebhookAction</c> read the status code off its <c>HttpResponseMessage</c> and dropped it,
/// leaving the buffered body to the finalizer on a path a workflow can fire on every content change.
///
/// <c>OllamaEmbeddingClient.EmbedAsync</c> caught everything and returned null, which is the right
/// answer for an unreachable backend and the wrong one for a cancelled request: an abandoned search
/// reported "no results" rather than stopping, and the caller could not tell the two apart.
/// </remarks>
public class OutboundHttpHygieneTests
{
    private static OutboundAddressGuard AllowingGuard() => new(
        resolve: (_, _) => Task.FromResult(new[] { IPAddress.Parse("203.0.113.10") }),
        isBlocked: _ => false);

    [Fact]
    public async Task A_webhook_disposes_the_response_it_read()
    {
        var body = new DisposalRecordingContent();
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = body };
        var action = new WebhookAction(
            new StubHttpClientFactory(new HttpClient(new StubHandler(response))),
            session: null!, // never reached: a non-Public document returns no data without a query
            AllowingGuard(),
            NullLogger<WebhookAction>.Instance);

        await action.ExecuteAsync(
            new Dictionary<string, string> { ["Url"] = "https://webhook.example/hook" },
            new Content { Id = Guid.NewGuid(), ContentType = "post", Sensitivity = SensitivityLevel.Sensitive },
            TestContext.Current.CancellationToken);

        body.Disposed.Should().BeTrue(
            "the response holds the buffered body, and a workflow can fire this on every content change");
    }

    [Fact]
    public async Task A_cancelled_embedding_call_throws_rather_than_reporting_no_results()
    {
        var client = new OllamaEmbeddingClient(
            new HttpClient(new StubHandler(new HttpResponseMessage(HttpStatusCode.OK))),
            Options.Create(new AiOptions { Enabled = true, EmbeddingBaseUrl = "http://embeddings.example" }));

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => client.EmbedAsync("anything", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "returning null here reports an empty index, which is a different answer from "
            + "\"this request was abandoned\"");
    }

    /// <summary>An unreachable backend still degrades to null rather than throwing.</summary>
    [Fact]
    public async Task An_unreachable_embedding_backend_still_returns_null()
    {
        var client = new OllamaEmbeddingClient(
            new HttpClient(new ThrowingHandler()),
            Options.Create(new AiOptions { Enabled = true, EmbeddingBaseUrl = "http://embeddings.example" }));

        var result = await client.EmbedAsync("anything", TestContext.Current.CancellationToken);

        result.Should().BeNull("a search must degrade to no results rather than 500 when the backend is down");
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("connection refused");
    }
}
