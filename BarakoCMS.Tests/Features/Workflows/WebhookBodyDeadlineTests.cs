using System.Diagnostics;
using System.Net;
using barakoCMS.Features.Workflows.Actions;
using barakoCMS.Infrastructure.Http;
using barakoCMS.Infrastructure.Security;
using barakoCMS.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// A receiver that sends its headers and then never finishes the body must not hold a workflow
/// run. The client's own timeout ends at the headers, so the body read carries its own deadline.
/// </summary>
public class WebhookBodyDeadlineTests
{
    private static readonly OutboundAddressGuard AllowingGuard = new(
        resolve: (_, _) => Task.FromResult(new[] { IPAddress.Parse("203.0.113.10") }),
        isBlocked: _ => false);

    private sealed class NeverEndingContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.CompletedTask;

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new HangingStream());
    }

    private sealed class HangingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }

    private sealed class HeadersThenHangHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new NeverEndingContent() });
    }

    [Fact]
    public async Task A_body_that_never_completes_fails_the_action_within_the_deadline()
    {
        using var client = new HttpClient(new HeadersThenHangHandler());
        var action = new WebhookAction(
            new StubHttpClientFactory(client),
            new Moq.Mock<Marten.IDocumentSession>().Object,
            new Moq.Mock<ISecretProtector>().Object,
            AllowingGuard,
            NullLogger<WebhookAction>.Instance)
        {
            BodyTimeout = TimeSpan.FromMilliseconds(300),
        };
        var content = new Content { Id = Guid.NewGuid(), ContentType = "post", Sensitivity = SensitivityLevel.Sensitive };

        var timer = Stopwatch.StartNew();
        var result = await action.RunAsync(
            new() { ["Url"] = "https://hooks.example.com/x" }, content, TestContext.Current.CancellationToken);
        timer.Stop();

        timer.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10), "the deadline, not the host stopping token, ended the read");
        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("did not arrive in time");
        result.ResponseStatus.Should().Be(200);
    }

    [Fact]
    public void The_default_deadline_is_the_shared_response_body_allowance()
    {
        var action = new WebhookAction(
            new StubHttpClientFactory(new HttpClient()),
            new Moq.Mock<Marten.IDocumentSession>().Object,
            new Moq.Mock<ISecretProtector>().Object,
            AllowingGuard,
            NullLogger<WebhookAction>.Instance);

        action.BodyTimeout.Should().Be(OutboundResilienceOptions.ResponseBodyAllowance);
    }
}
