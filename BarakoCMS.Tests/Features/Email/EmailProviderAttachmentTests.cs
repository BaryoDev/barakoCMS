using System.Text.Json;
using barakoCMS.Core.Interfaces;
using BarakoCMS.Email.Resend;
using BarakoCMS.Email.Smtp;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace BarakoCMS.Tests.Features.Email;

/// <summary>
/// An attachment handed to the SMTP provider is in the message the relay receives.
/// </summary>
/// <remarks>
/// Against the same loopback relay as <see cref="SmtpEmailServiceTests"/>, read from the DATA the
/// relay was sent, so the claim is about the message and not about a call to MimeKit.
/// </remarks>
public class SmtpAttachmentTests
{
    private static readonly byte[] Pdf = "%PDF-1.4 a receipt"u8.ToArray();

    private sealed class Snapshot(SmtpOptions options) : IOptionsSnapshot<SmtpOptions>
    {
        public SmtpOptions Value => options;
        public SmtpOptions Get(string? name) => options;
    }

    private sealed class NoStoredSettings : IEmailSettingsProvider
    {
        public Task<ResolvedEmailSettings> GetAsync(CancellationToken ct = default) =>
            Task.FromResult(new ResolvedEmailSettings(null, null, EmailSettingSource.None, EmailSettingSource.None));
    }

    private static SmtpEmailService Service(int port) => new(
        new Snapshot(new SmtpOptions
        {
            Host = "127.0.0.1",
            Port = port,
            From = "BarakoCMS <no-reply@example.com>",
            Security = SmtpSecurity.None,
        }),
        new NoStoredSettings());

    private static EmailAttachment Attachment(string name) => new()
    {
        FileName = name,
        ContentType = "application/pdf",
        Content = Pdf,
    };

    [Fact]
    public async Task An_attachment_reaches_the_relay_as_a_part_of_the_message()
    {
        using var relay = new FakeSmtpServer();

        await Service(relay.Port).SendEmailAsync(
            "guest@example.com", "Your receipt", "<p>Attached.</p>", [Attachment("receipt.pdf")],
            TestContext.Current.CancellationToken);

        var messages = relay.Messages;
        messages.Should().ContainSingle("one send is one message, or the assertions below run on nothing");
        messages[0].Should().Contain("guest@example.com");
        messages[0].Should().Contain("multipart/mixed");
        messages[0].Should().Contain("application/pdf");
        messages[0].Should().Contain("receipt.pdf");
        messages[0].Should().Contain(Convert.ToBase64String(Pdf), "the bytes travel base64 encoded, on one line at this size");
    }

    /// <summary>
    /// The control for the test above: the same send with no attachment carries none of it.
    /// </summary>
    [Fact]
    public async Task A_message_with_no_attachment_has_no_attachment_part()
    {
        using var relay = new FakeSmtpServer();

        await Service(relay.Port).SendEmailAsync(
            "guest@example.com", "Hello", "<p>Body</p>", TestContext.Current.CancellationToken);

        var messages = relay.Messages;
        messages.Should().ContainSingle();
        messages[0].Should().NotContain("multipart/mixed");
        messages[0].Should().NotContain("application/pdf");
    }

    /// <summary>
    /// The action cleans a name before it gets here. This is the provider on its own, handed a name
    /// that was not cleaned: the line break must not become a header of the message.
    /// </summary>
    [Fact]
    public async Task A_line_break_in_an_attachment_name_does_not_become_a_header()
    {
        using var relay = new FakeSmtpServer();

        await Service(relay.Port).SendEmailAsync(
            "guest@example.com", "Your receipt", "<p>Attached.</p>",
            [Attachment("receipt.pdf\r\nBcc: someone@evil.example")],
            TestContext.Current.CancellationToken);

        var messages = relay.Messages;
        messages.Should().ContainSingle();
        var lines = messages[0].Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        lines.Should().NotBeEmpty();
        lines.Should().NotContain(l => l.StartsWith("Bcc:", StringComparison.OrdinalIgnoreCase));
        messages[0].Should().Contain(Convert.ToBase64String(Pdf), "the attachment itself still went");
    }
}

/// <summary>
/// An attachment handed to the Resend provider is in the request it posts.
/// </summary>
public class ResendAttachmentTests
{
    private const string ApiKey = "re_test_ThisIsNotARealResendKey_0123456789";
    private static readonly byte[] Pdf = "%PDF-1.4 a receipt"u8.ToArray();

    private sealed class Settings : IEmailSettingsProvider
    {
        public Task<ResolvedEmailSettings> GetAsync(CancellationToken ct = default) =>
            Task.FromResult(new ResolvedEmailSettings(
                ApiKey, "BarakoCMS <hello@example.com>", EmailSettingSource.Configuration, EmailSettingSource.Configuration));
    }

    private static readonly EmailAttachment[] OneReceipt =
    [
        new() { FileName = "receipt.pdf", ContentType = "application/pdf", Content = Pdf },
    ];

    [Fact]
    public async Task An_attachment_is_posted_with_its_name_type_and_bytes()
    {
        var handler = new RecordingHandler();
        var service = new ResendEmailService(new HttpClient(handler), new Settings());

        await service.SendEmailAsync(
            "guest@example.com", "Your receipt", "<p>Attached.</p>", OneReceipt, TestContext.Current.CancellationToken);

        using var body = JsonDocument.Parse(handler.RequestBody);
        body.RootElement.GetProperty("to").EnumerateArray().Single().GetString().Should().Be("guest@example.com");

        var attachments = body.RootElement.GetProperty("attachments").EnumerateArray().ToList();
        attachments.Should().HaveCount(1);
        attachments[0].GetProperty("filename").GetString().Should().Be("receipt.pdf");
        attachments[0].GetProperty("content_type").GetString().Should().Be("application/pdf");
        Convert.FromBase64String(attachments[0].GetProperty("content").GetString()!).Should().Equal(Pdf);
    }

    [Fact]
    public async Task An_attachment_sent_for_a_tenant_is_posted_too()
    {
        var handler = new RecordingHandler();
        var service = new ResendEmailService(new HttpClient(handler), new Settings());

        await service.SendForTenantAsync(
            "club-a", "guest@example.com", "Your receipt", "<p>Attached.</p>", OneReceipt,
            TestContext.Current.CancellationToken);

        using var body = JsonDocument.Parse(handler.RequestBody);
        body.RootElement.GetProperty("attachments").EnumerateArray().ToList().Should().HaveCount(1);
    }

    /// <summary>A guard: the request for an email with no attachment is the one it was before.</summary>
    [Fact]
    public async Task A_message_with_no_attachment_posts_no_attachments_field()
    {
        var handler = new RecordingHandler();
        var service = new ResendEmailService(new HttpClient(handler), new Settings());

        await service.SendEmailAsync("guest@example.com", "Hello", "<p>Body</p>", TestContext.Current.CancellationToken);

        using var body = JsonDocument.Parse(handler.RequestBody);
        body.RootElement.TryGetProperty("attachments", out _).Should().BeFalse();
        body.RootElement.EnumerateObject().Select(p => p.Name).Should().Equal("from", "to", "subject", "html");
    }
}
