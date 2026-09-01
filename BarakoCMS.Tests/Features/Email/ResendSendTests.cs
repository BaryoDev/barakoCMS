using barakoCMS.Core.Interfaces;
using System.Net;
using System.Text.Json;
using BarakoCMS.Email.Resend;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace BarakoCMS.Tests.Features.Email;

/// <summary>Records the outbound request and answers with whatever the test scripted.</summary>
internal sealed class RecordingHandler : HttpMessageHandler
{
    private readonly HttpStatusCode _status;
    private readonly string _body;

    public RecordingHandler(HttpStatusCode status = HttpStatusCode.OK, string body = """{"id":"e_1"}""")
    {
        _status = status;
        _body = body;
    }

    public HttpRequestMessage? Request { get; private set; }

    public string RequestBody { get; private set; } = "";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Request = request;
        RequestBody = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        return new HttpResponseMessage(_status) { Content = new StringContent(_body) };
    }
}

/// <summary>
/// Where the Resend API key goes, and where it must not.
/// </summary>
/// <remarks>
/// The key is the one secret this module holds. It is a live sending credential for the whole
/// account, so anywhere it can end up as text is somewhere it can end up in a log aggregator, an
/// error tracker, or a support ticket. The two places worth pinning are the request the module makes
/// and the exception it raises when that request fails, because the failure path is the one that
/// gets written down.
///
/// Driven against the service with a stub handler rather than over HTTP. The claim is about the
/// bytes of the outbound request, and only this side of the client can see them.
/// </remarks>
public class ResendSendTests
{
    private const string ApiKey = "re_test_ThisIsNotARealResendKey_0123456789";

    private static ResendEmailService Service(RecordingHandler handler, string? apiKey, string? from = null) =>
        new(new HttpClient(handler), new StubSettings(apiKey, from));

    /// <summary>
    /// Stands in for the resolved settings, so these tests stay about the bytes on the wire.
    /// </summary>
    /// <remarks>
    /// The module reads credentials through <see cref="IEmailSettingsProvider"/> now rather than
    /// from IConfiguration, so that precedence lives in one place and an admin can set them without
    /// editing the deployment. Where a value came from is asserted against the real provider in
    /// EmailSettingsTests; stubbing it here would be the wrong test for the wrong claim.
    /// </remarks>
    private sealed record StubSettings(string? ApiKey, string? From) : IEmailSettingsProvider
    {
        public Task<ResolvedEmailSettings> GetAsync(CancellationToken ct = default) =>
            Task.FromResult(new ResolvedEmailSettings(
                ApiKey,
                From,
                ApiKey is null ? EmailSettingSource.None : EmailSettingSource.Configuration,
                From is null ? EmailSettingSource.None : EmailSettingSource.Configuration));
    }

    /// <summary>
    /// The positive control: a configured service sends what it was asked to send.
    /// </summary>
    [Fact]
    public async Task A_send_posts_the_message_to_resend()
    {
        var handler = new RecordingHandler();
        var service = Service(handler, ApiKey, "BarakoCMS <hello@example.com>");

        await service.SendEmailAsync("someone@example.com", "Your code", "<p>123456</p>", TestContext.Current.CancellationToken);

        handler.Request!.RequestUri!.ToString().Should().Be("https://api.resend.com/emails");
        using var body = JsonDocument.Parse(handler.RequestBody);
        body.RootElement.GetProperty("subject").GetString().Should().Be("Your code");
        body.RootElement.GetProperty("to").EnumerateArray().Single().GetString()
            .Should().Be("someone@example.com");
        body.RootElement.GetProperty("html").GetString().Should().Be("<p>123456</p>");
    }

    /// <summary>
    /// The key rides in the Authorization header and appears in no other part of the request.
    /// </summary>
    /// <remarks>
    /// A key in the query string is logged by every proxy on the way and by the receiving server's
    /// access log. A key in the body is logged by anything that records request payloads.
    /// </remarks>
    [Fact]
    public async Task The_api_key_travels_only_as_a_bearer_header()
    {
        var handler = new RecordingHandler();

        await Service(handler, ApiKey)
            .SendEmailAsync("someone@example.com", "s", "b", TestContext.Current.CancellationToken);

        handler.Request!.Headers.Authorization!.Scheme.Should().Be("Bearer");
        handler.Request.Headers.Authorization.Parameter.Should().Be(ApiKey,
            "the control: it does have to be sent, or nothing would be authenticated");

        handler.Request.RequestUri!.ToString().Should().NotContain(ApiKey);
        handler.RequestBody.Should().NotContain(ApiKey);
    }

    /// <summary>
    /// A rejected send says what Resend said, and does not repeat the credential back.
    /// </summary>
    /// <remarks>
    /// This message reaches a log line at every call site, so it is the most likely place for the key
    /// to escape into somewhere it will sit for a long time.
    /// </remarks>
    [Fact]
    public async Task A_rejected_send_reports_the_failure_without_the_key_in_it()
    {
        var handler = new RecordingHandler(
            HttpStatusCode.Forbidden, """{"message":"API key is invalid","name":"validation_error"}""");

        var send = async () => await Service(handler, ApiKey)
            .SendEmailAsync("someone@example.com", "s", "b", TestContext.Current.CancellationToken);

        var thrown = await send.Should().ThrowAsync<InvalidOperationException>();
        thrown.Which.Message.Should().Contain("403", "the caller has to be able to tell why it failed");
        thrown.Which.Message.Should().NotContain(ApiKey);
        thrown.Which.ToString().Should().NotContain(ApiKey,
            "the whole exception is what gets logged, not just its message");
    }

    /// <summary>
    /// With no key configured the send fails loudly rather than quietly doing nothing.
    /// </summary>
    /// <remarks>
    /// Silence here is the dangerous answer. The caller would be told the mail went out and the
    /// person waiting for a sign-in code would wait forever, with nothing anywhere saying why.
    /// </remarks>
    [Fact]
    public async Task An_unconfigured_service_refuses_rather_than_pretending_to_send()
    {
        var handler = new RecordingHandler();
        var previous = Environment.GetEnvironmentVariable("RESEND_API_KEY");
        Environment.SetEnvironmentVariable("RESEND_API_KEY", null);
        try
        {
            var send = async () => await Service(handler, null)
                .SendEmailAsync("someone@example.com", "s", "b", TestContext.Current.CancellationToken);

            (await send.Should().ThrowAsync<InvalidOperationException>())
                .Which.Message.Should().Contain("Resend:ApiKey", "the message names the setting to fix");
            handler.Request.Should().BeNull("nothing may be sent without a credential");
        }
        finally
        {
            Environment.SetEnvironmentVariable("RESEND_API_KEY", previous);
        }
    }
}
