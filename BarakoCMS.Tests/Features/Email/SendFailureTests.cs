using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using barakoCMS.Core.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace BarakoCMS.Tests.Features.Email;

/// <summary>Fails every send, the way a provider outage or a revoked key does.</summary>
internal sealed class FailingEmailService : IEmailService
{
    public int Attempts { get; private set; }

    public Task SendEmailAsync(string to, string subject, string body, CancellationToken cancellationToken = default)
    {
        Attempts++;
        throw new HttpRequestException("Resend answered 503 (this is the test provider being down).");
    }
}

/// <summary>
/// A provider outage must not become a failed request at the call sites that treat mail as best
/// effort.
/// </summary>
/// <remarks>
/// Registration is the one that matters. Its whole design is that every path answers the same
/// sentence: an address that is free, an address somebody else already registered, and a request
/// that fell over all look identical from outside, because the difference is what tells an attacker
/// whether an address has an account. A send that escaped as a 500 would reintroduce that difference
/// without anyone changing the message, which is the shape of leak nobody reads a diff for.
///
/// Each assertion is paired against a working provider. "It returned 200" proves nothing on its own,
/// and neither does "the bodies match" if the endpoint answers the same thing to everything: the
/// pairing is what says the outage is invisible rather than that the endpoint is blind.
/// </remarks>
[Collection("Sequential")]
public class SendFailureTests
{
    private readonly IntegrationTestFixture _factory;

    public SendFailureTests(IntegrationTestFixture factory) => _factory = factory;

    /// <summary>A host whose only difference is that email always fails.</summary>
    private (HttpClient Client, FailingEmailService Email) BrokenEmailHost()
    {
        var failing = new FailingEmailService();

        var derived = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IEmailService>();
                services.AddSingleton<IEmailService>(failing);
            }));

        return (derived.CreateClient(), failing);
    }

    private static object Registration(string email) => new
    {
        username = "u" + Guid.NewGuid().ToString("N")[..10],
        email,
        password = "Correct-Horse-Battery-9!",
    };

    [Fact]
    public async Task Registration_answers_the_same_thing_when_the_provider_is_down()
    {
        var (broken, failing) = BrokenEmailHost();
        var working = _factory.CreateClient();

        var down = await broken.PostAsJsonAsync("/api/auth/register",
            Registration($"down-{Guid.NewGuid():N}@example.com"), TestContext.Current.CancellationToken);

        var up = await working.PostAsJsonAsync("/api/auth/register",
            Registration($"up-{Guid.NewGuid():N}@example.com"), TestContext.Current.CancellationToken);

        // The provider really was called, or this test is about a code path nothing reached.
        failing.Attempts.Should().BeGreaterThan(0,
            "a registration that never tried to send would pass this test while proving nothing");

        up.StatusCode.Should().Be(HttpStatusCode.OK, "the working case has to work, or the comparison is empty");
        down.StatusCode.Should().Be(HttpStatusCode.OK, await down.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        var downBody = await down.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var upBody = await up.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        downBody.Should().Be(upBody,
            "the answer is deliberately identical for every outcome, and an outage is one more outcome");
    }

    [Fact]
    public async Task A_failed_send_does_not_put_the_reason_in_the_response()
    {
        var (broken, _) = BrokenEmailHost();

        var response = await broken.PostAsJsonAsync("/api/auth/register",
            Registration($"quiet-{Guid.NewGuid():N}@example.com"), TestContext.Current.CancellationToken);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        body.Should().NotContainEquivalentOf("resend");
        body.Should().NotContainEquivalentOf("503");
        body.Should().NotContainEquivalentOf("HttpRequestException");
        body.Should().NotContainEquivalentOf("stack");
    }

    /// <summary>
    /// A sign-in code request answers the same sentence to everything, an outage included.
    /// </summary>
    /// <remarks>
    /// This is the reverse of what it looks like it should be. <c>OtpService</c> does report a failed
    /// send, and <c>RequestEndpoint</c> deliberately throws that result away, because the route is
    /// anonymous: an error that only appears for addresses that exist is an account enumeration
    /// oracle, and enumeration outranks the better message on a route anybody can call. The failure
    /// is logged at Error, where the operator is.
    ///
    /// Three answers are compared here, not two. A registered address, an address nobody has, and a
    /// registered address while the provider is down all have to be indistinguishable. Comparing only
    /// two of them would pass against an endpoint that leaked on the third.
    /// </remarks>
    [Fact]
    public async Task A_sign_in_code_request_answers_the_same_thing_whether_it_was_sent_or_not()
    {
        var (broken, failing) = BrokenEmailHost();
        var working = _factory.CreateClient();

        var registered = await SignedUpEmailAsync();
        var unknown = $"nobody-{Guid.NewGuid():N}@example.com";

        var sent = await Request(working, registered);
        var notSent = await Request(broken, registered);
        var noAccount = await Request(working, unknown);

        failing.Attempts.Should().BeGreaterThan(0,
            "the outage case has to have actually tried to send, or it is the no-account case wearing a hat");

        sent.Status.Should().Be(HttpStatusCode.OK);
        notSent.Status.Should().Be(HttpStatusCode.OK,
            "a provider outage is not something an anonymous caller gets to learn about");
        noAccount.Status.Should().Be(HttpStatusCode.OK);

        notSent.Body.Should().Be(sent.Body, "an outage must not be visible from outside");
        noAccount.Body.Should().Be(sent.Body, "and neither must whether the address has an account");

        notSent.Body.Should().NotContainEquivalentOf("resend");
        notSent.Body.Should().NotContainEquivalentOf("503");
    }

    private static async Task<(HttpStatusCode Status, string Body)> Request(HttpClient client, string email)
    {
        var response = await client.PostAsJsonAsync("/api/auth/otp/request",
            new { email }, TestContext.Current.CancellationToken);

        return (response.StatusCode,
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>An address with an account behind it, so the OTP path is actually entered.</summary>
    private async Task<string> SignedUpEmailAsync()
    {
        var (_, userId) = await TestHelpers.CreateAdminUserAsync(_factory);

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<Marten.IQuerySession>();
        var user = await session.LoadAsync<barakoCMS.Models.User>(userId);

        user.Should().NotBeNull("the OTP path is only entered for an address that has an account");
        return user!.Email;
    }
}
