using System.Net;
using System.Net.Sockets;
using barakoCMS.Core.Interfaces;
using BarakoCMS.Email.Resend;
using BarakoCMS.Email.Smtp;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace BarakoCMS.Tests.Features.Email;

/// <summary>
/// When the shipped providers say a message was not sent. A caller may send again on
/// <see cref="EmailNotSentException"/>, so each case that may have delivered is paired with one that
/// cannot have, against the same relay or handler.
/// </summary>
public class EmailNotSentTests
{
    private const string Password = "hunter2-the-relay-password";
    private const string ApiKey = "re_test_ThisIsNotARealResendKey_0123456789";

    [Fact]
    public async Task An_smtp_relay_that_refuses_the_connection_is_not_sent()
    {
        var port = ClosedPort();

        var send = () => Smtp(port).SendEmailAsync("someone@example.com", "s", "<p>b</p>", Ct);

        var failure = (await send.Should().ThrowAsync<EmailNotSentException>()).Which;
        failure.Message.Should().Contain($"127.0.0.1:{port}");
    }

    [Fact]
    public async Task An_smtp_recipient_refused_before_data_is_not_sent()
    {
        using var relay = new FakeSmtpServer(recipientFailure: "450 4.2.1 Mailbox busy, try later");

        var send = () => Smtp(relay.Port).SendEmailAsync("someone@example.com", "s", "<p>b</p>", Ct);

        var failure = (await send.Should().ThrowAsync<EmailNotSentException>()).Which;
        failure.Message.Should().Contain("450");
        relay.Messages.Should().BeEmpty("the relay refused the recipient before DATA");
    }

    [Fact]
    public async Task An_smtp_login_refused_is_not_sent_and_keeps_the_password_out()
    {
        using var relay = new FakeSmtpServer(authFailure: $"535 5.7.8 Bad credentials for postmaster/{Password}");

        var send = () => Smtp(relay.Port).SendEmailAsync("someone@example.com", "s", "<p>b</p>", Ct);

        var failure = (await send.Should().ThrowAsync<EmailNotSentException>()).Which;
        failure.Message.Should().Contain("535");
        failure.ToString().Should().NotContain(Password);
        failure.InnerException.Should().BeNull("the relay's own exception carries its words unredacted");
    }

    /// <summary>
    /// The relay has the whole message and drops the line before answering. It may deliver it, so
    /// this must not be the exception a caller sends again on.
    /// </summary>
    [Fact]
    public async Task An_smtp_message_the_relay_took_before_dropping_the_line_is_not_reported_as_not_sent()
    {
        using var relay = new FakeSmtpServer(afterData: AfterData.Drop);

        var send = () => Smtp(relay.Port).SendEmailAsync("someone@example.com", "s", "<p>b</p>", Ct);

        var failure = (await send.Should().ThrowAsync<InvalidOperationException>()).Which;
        failure.Should().NotBeOfType<EmailNotSentException>();
        relay.Messages.Should().ContainSingle("the relay received the message, which is why this is not 'not sent'");
    }

    [Fact]
    public async Task An_smtp_message_the_relay_took_and_never_answered_times_out_without_saying_not_sent()
    {
        using var relay = new FakeSmtpServer(afterData: AfterData.Hang);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));

        var send = () => Smtp(relay.Port).SendEmailAsync("someone@example.com", "s", "<p>b</p>", timeout.Token);

        await send.Should().ThrowAsync<OperationCanceledException>();
        relay.Messages.Should().ContainSingle();
    }

    [Fact]
    public async Task A_resend_connection_that_was_never_made_is_not_sent()
    {
        var handler = new StepHandler((_, _) =>
            throw new HttpRequestException(HttpRequestError.ConnectionError, "Connection refused (api.resend.com:443)"));

        var send = () => Resend(handler).SendEmailAsync("someone@example.com", "s", "<p>b</p>", Ct);

        var failure = (await send.Should().ThrowAsync<EmailNotSentException>()).Which;
        failure.Message.Should().Contain("ConnectionError");
        failure.ToString().Should().NotContain(ApiKey);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task A_resend_refusal_before_acceptance_is_not_sent(HttpStatusCode status)
    {
        var handler = new StepHandler((_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("slow down") }));

        var send = () => Resend(handler).SendEmailAsync("someone@example.com", "s", "<p>b</p>", Ct);

        var failure = (await send.Should().ThrowAsync<EmailNotSentException>()).Which;
        failure.Message.Should().Contain(((int)status).ToString());
    }

    [Fact]
    public async Task A_resend_500_is_not_reported_as_not_sent()
    {
        var handler = new StepHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("oops") }));

        var send = () => Resend(handler).SendEmailAsync("someone@example.com", "s", "<p>b</p>", Ct);

        var failure = (await send.Should().ThrowAsync<InvalidOperationException>()).Which;
        failure.Should().NotBeOfType<EmailNotSentException>("a 500 can come after Resend queued the message");
    }

    [Fact]
    public async Task A_resend_connection_that_ended_after_the_request_is_not_reported_as_not_sent()
    {
        var handler = new StepHandler((_, _) =>
            throw new HttpRequestException(HttpRequestError.ResponseEnded, "The response ended prematurely."));

        var send = () => Resend(handler).SendEmailAsync("someone@example.com", "s", "<p>b</p>", Ct);

        var failure = (await send.Should().ThrowAsync<HttpRequestException>()).Which;
        failure.HttpRequestError.Should().Be(HttpRequestError.ResponseEnded);
    }

    [Fact]
    public async Task A_resend_send_accepted_and_then_timed_out_does_not_say_not_sent()
    {
        var handler = new StepHandler(async (_, token) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(200));

        var send = () => Resend(handler).SendEmailAsync("someone@example.com", "s", "<p>b</p>", timeout.Token);

        await send.Should().ThrowAsync<OperationCanceledException>();
        handler.Calls.Should().Be(1, "the request went out, which is what makes this unknown rather than not sent");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static int ClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static SmtpEmailService Smtp(int port) =>
        new(new Snapshot(new SmtpOptions
        {
            Host = "127.0.0.1",
            Port = port,
            User = "postmaster",
            Password = Password,
            From = "BarakoCMS <no-reply@example.com>",
            Security = SmtpSecurity.None,
        }), new Settings(null));

    private static ResendEmailService Resend(HttpMessageHandler handler) =>
        new(new HttpClient(handler), new Settings(ApiKey));

    private sealed class Snapshot(SmtpOptions options) : IOptionsSnapshot<SmtpOptions>
    {
        public SmtpOptions Value => options;
        public SmtpOptions Get(string? name) => options;
    }

    private sealed class Settings(string? apiKey) : IEmailSettingsProvider
    {
        public Task<ResolvedEmailSettings> GetAsync(CancellationToken ct = default) =>
            Task.FromResult(new ResolvedEmailSettings(
                apiKey,
                null,
                apiKey is null ? EmailSettingSource.None : EmailSettingSource.Configuration,
                EmailSettingSource.None));
    }

    private sealed class StepHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> step) : HttpMessageHandler
    {
        private int _calls;

        public int Calls => _calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return step(request, ct);
        }
    }
}
