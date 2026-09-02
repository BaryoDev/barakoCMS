using barakoCMS.Core.Interfaces;
using BarakoCMS.Email.Smtp;
using FluentAssertions;
using MailKit.Security;
using Microsoft.Extensions.Options;
using Xunit;

namespace BarakoCMS.Tests.Features.Email;

/// <summary>
/// What the SMTP provider does against a relay, including the two things it must never say.
/// </summary>
/// <remarks>
/// No database and no Docker here: the service reads its settings from its own configuration
/// section and the from address from a provider, and both are supplied directly. The relay is a
/// real socket on loopback.
/// </remarks>
public class SmtpEmailServiceTests
{
    private const string Password = "hunter2-the-relay-password";
    private const string ApiKey = "re_a_resend_key_that_has_nothing_to_do_with_smtp";

    private sealed class Snapshot(SmtpOptions options) : IOptionsSnapshot<SmtpOptions>
    {
        public SmtpOptions Value => options;
        public SmtpOptions Get(string? name) => options;
    }

    /// <summary>Stands in for the core resolver, which needs a database this test does not have.</summary>
    private sealed class Settings(string? from, EmailSettingSource source, string? apiKey = null) : IEmailSettingsProvider
    {
        public Task<ResolvedEmailSettings> GetAsync(CancellationToken ct = default) =>
            Task.FromResult(new ResolvedEmailSettings(
                apiKey,
                from,
                apiKey is null ? EmailSettingSource.None : EmailSettingSource.Configuration,
                source));
    }

    private static SmtpEmailService Service(SmtpOptions options, IEmailSettingsProvider? settings = null) =>
        new(new Snapshot(options), settings ?? new Settings(null, EmailSettingSource.None));

    private static SmtpOptions Options(int port) => new()
    {
        Host = "127.0.0.1",
        Port = port,
        User = "postmaster",
        Password = Password,
        From = "BarakoCMS <no-reply@example.com>",
        Security = SmtpSecurity.None,
    };

    /// <summary>
    /// The case that must work. Everything below is about a refusal, and a refusal test passes
    /// against a provider that can never send anything at all.
    /// </summary>
    [Fact]
    public async Task A_message_reaches_the_relay()
    {
        using var relay = new FakeSmtpServer();

        await Service(Options(relay.Port))
            .SendEmailAsync("someone@example.com", "Hello from barakoCMS", "<p>Body</p>",
                TestContext.Current.CancellationToken);

        var messages = relay.Messages;
        messages.Should().ContainSingle("one send is one message, or the assertions below run on nothing");
        messages[0].Should().Contain("someone@example.com");
        messages[0].Should().Contain("Hello from barakoCMS");
        messages[0].Should().Contain("no-reply@example.com");
    }

    /// <summary>
    /// A relay that quotes the credentials back must not get them into an admin screen.
    /// </summary>
    /// <remarks>
    /// The failure message is the operator's only clue about which of five settings is wrong, so it
    /// carries what the relay said. A relay says whatever it likes, and this one echoes the login it
    /// just rejected, which is the shape that turns a helpful message into a leak: the admin test
    /// send returns it, the workflow action logs it, and it ends up in a support ticket.
    ///
    /// Both halves are asserted. Without the second, blanking the whole message would pass.
    /// </remarks>
    [Fact]
    public async Task A_rejected_login_does_not_put_the_password_in_the_failure()
    {
        using var relay = new FakeSmtpServer(
            authFailure: $"535 5.7.8 Bad credentials for postmaster/{Password}");

        var send = async () => await Service(Options(relay.Port))
            .SendEmailAsync("someone@example.com", "s", "<p>b</p>", TestContext.Current.CancellationToken);

        var failure = (await send.Should().ThrowAsync<InvalidOperationException>()).Which;

        failure.Message.Should().NotContain(Password, "the relay echoed it and we are the last stop");
        failure.Message.Should().Contain("535", "the reason has to survive, or the message is useless");
        failure.Message.Should().Contain("127.0.0.1", "and so does which relay refused it");
    }

    /// <summary>
    /// The other secret in reach. The service never touches <c>ResolvedEmailSettings.ApiKey</c>, and
    /// this is what says so from outside.
    /// </summary>
    [Fact]
    public async Task A_send_failure_does_not_carry_the_email_api_key()
    {
        using var relay = new FakeSmtpServer(authFailure: "535 5.7.8 Nope");

        var settings = new Settings("stored@example.com", EmailSettingSource.Stored, ApiKey);

        var send = async () => await Service(Options(relay.Port), settings)
            .SendEmailAsync("someone@example.com", "s", "<p>b</p>", TestContext.Current.CancellationToken);

        var failure = (await send.Should().ThrowAsync<InvalidOperationException>()).Which;

        failure.Message.Should().Contain("535", "the failure is the one this test is about");
        failure.Message.Should().NotContain(ApiKey);
        failure.Message.Should().NotContain("re_", "nor a fragment of one");
    }

    /// <summary>
    /// A from address typed into the admin wins, because that field is not provider-specific and a
    /// person set it most recently. The module's own From is the fallback, not the other way round.
    /// </summary>
    [Fact]
    public async Task A_stored_from_address_wins_over_the_module_configuration()
    {
        using var relay = new FakeSmtpServer();

        await Service(Options(relay.Port), new Settings("typed-in@example.com", EmailSettingSource.Stored))
            .SendEmailAsync("someone@example.com", "s", "<p>b</p>", TestContext.Current.CancellationToken);

        var messages = relay.Messages;
        messages.Should().ContainSingle();
        messages[0].Should().Contain("typed-in@example.com");
        messages[0].Should().NotContain("no-reply@example.com", "the configured sender was overridden, not appended");
    }

    /// <summary>
    /// And a from address that only the deployment configured does not, because that one arrives
    /// from <c>Resend:From</c> and this is not Resend.
    /// </summary>
    [Fact]
    public async Task A_configured_from_address_from_another_provider_does_not_win()
    {
        using var relay = new FakeSmtpServer();

        await Service(Options(relay.Port), new Settings("resend-from@example.com", EmailSettingSource.Configuration))
            .SendEmailAsync("someone@example.com", "s", "<p>b</p>", TestContext.Current.CancellationToken);

        var messages = relay.Messages;
        messages.Should().ContainSingle();
        messages[0].Should().Contain("no-reply@example.com");
    }

    /// <summary>
    /// With no sender anywhere the send refuses rather than inventing one, and says both places it
    /// looked.
    /// </summary>
    [Fact]
    public async Task No_sender_anywhere_is_refused_with_both_places_named()
    {
        using var relay = new FakeSmtpServer();

        var options = Options(relay.Port);
        options.From = null;

        var send = async () => await Service(options)
            .SendEmailAsync("someone@example.com", "s", "<p>b</p>", TestContext.Current.CancellationToken);

        var failure = (await send.Should().ThrowAsync<InvalidOperationException>()).Which;
        failure.Message.Should().Contain("Modules:Email.Smtp:From");
        relay.Messages.Should().BeEmpty("nothing should have gone out with a sender nobody chose");
    }

    /// <summary>
    /// The default refuses a relay that will not encrypt, rather than falling back to plaintext.
    /// </summary>
    /// <remarks>
    /// This is the whole reason the default is not MailKit's <c>Auto</c>. Auto resolves to
    /// StartTlsWhenAvailable off port 465, so a relay that stops advertising STARTTLS gets the
    /// password in the clear and nothing anywhere says so. The fake relay advertises no STARTTLS,
    /// which is exactly that server.
    ///
    /// Paired with the send above, which is the same relay and the same credentials with
    /// <c>Security = None</c> asked for by name. Without that pair this would pass against a
    /// provider that cannot connect to anything.
    /// </remarks>
    [Fact]
    public async Task An_unset_security_mode_refuses_a_relay_that_will_not_encrypt()
    {
        using var relay = new FakeSmtpServer();

        var options = Options(relay.Port);
        options.Security = null;

        var send = async () => await Service(options)
            .SendEmailAsync("someone@example.com", "s", "<p>b</p>", TestContext.Current.CancellationToken);

        await send.Should().ThrowAsync<InvalidOperationException>();

        relay.Messages.Should().BeEmpty("a message that went out in the clear is the bug this guards");
    }

    [Theory]
    [InlineData(587, null, SecureSocketOptions.StartTls)]
    [InlineData(25, null, SecureSocketOptions.StartTls)]
    [InlineData(465, null, SecureSocketOptions.SslOnConnect)]
    [InlineData(465, SmtpSecurity.StartTls, SecureSocketOptions.StartTls)]
    [InlineData(587, SmtpSecurity.None, SecureSocketOptions.None)]
    [InlineData(587, SmtpSecurity.SslOnConnect, SecureSocketOptions.SslOnConnect)]
    public void The_security_mode_falls_back_to_the_port_and_never_to_plaintext(
        int port, SmtpSecurity? security, SecureSocketOptions expected)
    {
        SmtpEmailService.SecurityFor(new SmtpOptions { Port = port, Security = security })
            .Should().Be(expected);
    }
}
