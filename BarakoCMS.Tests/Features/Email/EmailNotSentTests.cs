using System.Net;
using System.Net.Sockets;
using barakoCMS.Core.Interfaces;
using BarakoCMS.Email.Smtp;
using MailKit.Net.Smtp;
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
        failure.Message.Should().Contain("Mailbox busy", "the relay's refusal is named");
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

    /// <summary>
    /// A relay that accepts the connection and never greets. MailKit's own limit was 120 s per read,
    /// so three tries of this ran past the workflow lease.
    /// </summary>
    [Fact]
    public async Task An_smtp_relay_that_never_answers_is_not_sent_within_the_timeout()
    {
        var silent = new TcpListener(IPAddress.Loopback, 0);
        silent.Start();
        try
        {
            var port = ((IPEndPoint)silent.LocalEndpoint).Port;
            using var caller = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            caller.CancelAfter(TimeSpan.FromSeconds(10));

            var send = () => Smtp(port, timeoutSeconds: 1).SendEmailAsync("someone@example.com", "s", "<p>b</p>", caller.Token);

            var failure = (await send.Should().ThrowAsync<EmailNotSentException>()).Which;
            failure.Message.Should().Contain("TimeoutException");
        }
        finally
        {
            silent.Stop();
        }
    }

    [Fact]
    public void The_smtp_bound_on_a_not_sent_send_is_three_timeouts()
    {
        Smtp(25, timeoutSeconds: 7).MaxNotSentDuration.Should().Be(TimeSpan.FromSeconds(21));
        ((IEmailService)Smtp(25)).MaxNotSentDuration.Should().Be(TimeSpan.FromSeconds(90));
    }

    /// <summary>
    /// The relay answered 250 to the message, so it was sent. A failure closing the connection after
    /// that must not fail the send, or the durable queue sends a second copy.
    /// </summary>
    [Fact]
    public async Task An_smtp_message_the_relay_accepted_succeeds_when_closing_the_connection_fails()
    {
        using var relay = new FakeSmtpServer();

        await Smtp(relay.Port, newClient: () => new DisconnectFailsClient())
            .SendEmailAsync("someone@example.com", "s", "<p>b</p>", Ct);

        relay.Messages.Should().ContainSingle();
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

    private static SmtpEmailService Smtp(int port, double timeoutSeconds = SmtpOptions.DefaultTimeoutSeconds, Func<SmtpClient>? newClient = null) =>
        new(new Snapshot(new SmtpOptions
        {
            Host = "127.0.0.1",
            Port = port,
            User = "postmaster",
            Password = Password,
            From = "BarakoCMS <no-reply@example.com>",
            Security = SmtpSecurity.None,
            TimeoutSeconds = timeoutSeconds,
        }), new Settings(null), null, newClient ?? (() => new SmtpClient()));

    private sealed class DisconnectFailsClient : SmtpClient
    {
        public override Task DisconnectAsync(bool quit, CancellationToken cancellationToken = default) =>
            throw new IOException("The connection was reset while closing.");
    }

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
}
