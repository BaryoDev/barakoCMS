using System.Net;
using System.Security.Cryptography;
using System.Text;
using Xunit;
using FluentAssertions;

namespace BarakoCMS.Tests;

/// <summary>
/// The receiver is anonymous by necessity: Resend is not going to hold a token. So the signature
/// is the only thing standing between a stranger and a write, and the guard used to read
/// "if a secret is configured AND the signature is bad, reject", which accepts everything when no
/// secret is set. The payload marks an address bounced or complained, so a forged post can get a
/// real recipient suppressed.
/// </summary>
[Collection("Sequential")]
public class ResendWebhookTests
{
    private const string Secret = "whsec_" + "dGVzdC1zZWNyZXQtZm9yLXN2aXgtc2lnbmluZy0xMjM0NTY3OA==";
    private readonly IntegrationTestFixture _factory;

    public ResendWebhookTests(IntegrationTestFixture factory) => _factory = factory;

    private static string Body() =>
        """{"type":"email.bounced","data":{"to":["victim@example.com"],"email_id":"e1"}}""";

    private static HttpRequestMessage Signed(string body, string? secret)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/resend")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        var id = "msg_test";
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        req.Headers.Add("svix-id", id);
        req.Headers.Add("svix-timestamp", ts);

        if (secret is not null)
        {
            var key = Convert.FromBase64String(secret["whsec_".Length..]);
            using var hmac = new HMACSHA256(key);
            var sig = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{id}.{ts}.{body}")));
            req.Headers.Add("svix-signature", "v1," + sig);
        }
        else
        {
            req.Headers.Add("svix-signature", "v1,not-a-real-signature");
        }
        return req;
    }

    // The regression. With no secret configured the endpoint used to accept this.
    [Fact]
    public async Task An_unconfigured_receiver_refuses_the_post_rather_than_trusting_it()
    {
        // Deliberately not disposed. A derived WebApplicationFactory shares the parent's server,
        // so disposing it tears down the shared fixture host and every later test in the run fails
        // for reasons that have nothing to do with them. The fixture owns the lifetime.
        // WithSetting clears the configuration key, but HandleAsync falls back to the process
        // environment. If RESEND_WEBHOOK_SECRET is exported the receiver is configured after all,
        // this request is refused for its forged signature instead, and the test passes while
        // measuring nothing. Refuse to run rather than report a result that does not mean what it
        // says. Mutation-tested locally: reverting the fix turns this red, so the variable is unset
        // here. That is a fact about one machine, which is exactly why it is asserted rather than
        // assumed.
        Environment.GetEnvironmentVariable("RESEND_WEBHOOK_SECRET").Should().BeNull(
            "this test can only observe the unconfigured path when the environment fallback is also "
          + "unset; unset RESEND_WEBHOOK_SECRET before running it");

        var f = _factory.WithSetting("Resend:WebhookSecret", null);
        var client = f.CreateClient();

        var res = await client.SendAsync(Signed(Body(), secret: null));

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "an unconfigured receiver cannot tell a real Resend post from a forged one, so it must refuse");
    }

    // The control, on the signature check itself rather than through a web host.
    //
    // A guard that refuses everything would satisfy the regression test above, so something has to
    // prove a genuine post is still accepted. Doing that through the fixture proved unreliable for
    // reasons unrelated to this code: the first derived host in a class answers with an empty 404,
    // and the handler never returns 404 (it sends only 401 or 200), so that was the harness. The
    // security decision lives in VerifySvix, so the control belongs there, where it is deterministic.
    [Fact]
    public void A_correct_signature_verifies()
    {
        const string id = "msg_1";
        const string ts = "1700000000";
        var body = Body();

        var key = Convert.FromBase64String(Secret["whsec_".Length..]);
        using var hmac = new HMACSHA256(key);
        var sig = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{id}.{ts}.{body}")));

        BarakoCMS.Email.Resend.ResendWebhookEndpoint
            .VerifySvix(Secret, body, id, ts, "v1," + sig)
            .Should().BeTrue("a correctly signed payload must verify, or the guard refuses everything");
    }

    [Theory]
    [InlineData("v1,not-a-real-signature", "a forged signature")]
    [InlineData("", "no signature header at all")]
    public void A_bad_signature_does_not_verify(string sigHeader, string why)
    {
        BarakoCMS.Email.Resend.ResendWebhookEndpoint
            .VerifySvix(Secret, Body(), "msg_1", "1700000000", sigHeader)
            .Should().BeFalse(why + " must not verify");
    }

    [Fact]
    public void A_tampered_body_does_not_verify()
    {
        const string id = "msg_1";
        const string ts = "1700000000";

        var key = Convert.FromBase64String(Secret["whsec_".Length..]);
        using var hmac = new HMACSHA256(key);
        var sig = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{id}.{ts}.{Body()}")));

        var tampered = Body().Replace("victim@example.com", "someone-else@example.com");

        BarakoCMS.Email.Resend.ResendWebhookEndpoint
            .VerifySvix(Secret, tampered, id, ts, "v1," + sig)
            .Should().BeFalse("the signature covers the body, so swapping the recipient must invalidate it");
    }
}
