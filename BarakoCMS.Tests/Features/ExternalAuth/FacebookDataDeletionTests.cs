using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BarakoCMS.ExternalAuth;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.ExternalAuth;

internal static class SignedRequests
{
    public const string Secret = "fb-deletion-secret";

    public static string Encode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>A signed_request built the way Facebook builds it.</summary>
    public static string Sign(string payloadJson, string secret = Secret)
    {
        var payload = Encode(Encoding.UTF8.GetBytes(payloadJson));
        var signature = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.ASCII.GetBytes(payload));
        return $"{Encode(signature)}.{payload}";
    }

    public static string For(string userId, string algorithm = "HMAC-SHA256", string secret = Secret, long? issuedAt = null) =>
        Sign($$"""{"algorithm":"{{algorithm}}","user_id":"{{userId}}","issued_at":{{issuedAt ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()}}}""", secret);
}

/// <summary>The signed_request check on its own: every way a request can be wrong, and one that is right.</summary>
public class FacebookSignedRequestTests
{
    [Fact]
    public void A_request_signed_with_the_secret_yields_the_user_id()
    {
        FacebookSignedRequest.UserIdOf(SignedRequests.For("1234567"), SignedRequests.Secret).Should().Be("1234567");
    }

    [Fact]
    public void A_request_signed_with_another_secret_yields_nothing()
    {
        FacebookSignedRequest.UserIdOf(SignedRequests.For("1234567", secret: "other"), SignedRequests.Secret).Should().BeNull();
    }

    [Fact]
    public void A_request_with_a_changed_payload_yields_nothing()
    {
        var parts = SignedRequests.For("1234567").Split('.');
        var forged = parts[0] + "." + SignedRequests.Encode(Encoding.UTF8.GetBytes("""{"algorithm":"HMAC-SHA256","user_id":"999"}"""));

        FacebookSignedRequest.UserIdOf(forged, SignedRequests.Secret).Should().BeNull();
    }

    [Theory]
    [InlineData("HMAC-SHA1")]
    [InlineData("none")]
    [InlineData("")]
    public void A_correctly_signed_request_with_the_wrong_algorithm_yields_nothing(string algorithm)
    {
        FacebookSignedRequest.UserIdOf(SignedRequests.For("1234567", algorithm), SignedRequests.Secret).Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("nodot")]
    [InlineData("a.b.c")]
    [InlineData("!!!.???")]
    [InlineData("abc=.def=")]
    [InlineData("a.b")]
    public void Malformed_input_yields_nothing(string input)
    {
        FacebookSignedRequest.UserIdOf(input, SignedRequests.Secret).Should().BeNull();
    }

    [Fact]
    public void A_signed_payload_that_is_not_json_yields_nothing()
    {
        FacebookSignedRequest.UserIdOf(SignedRequests.Sign("not json"), SignedRequests.Secret).Should().BeNull();
        FacebookSignedRequest.UserIdOf(SignedRequests.Sign("""{"algorithm":"HMAC-SHA256"}"""), SignedRequests.Secret).Should().BeNull();
        FacebookSignedRequest.UserIdOf(SignedRequests.Sign("""{"algorithm":"HMAC-SHA256","user_id":42}"""), SignedRequests.Secret).Should().BeNull();
    }

    [Fact]
    public void An_oversized_request_yields_nothing_even_when_validly_signed()
    {
        var big = SignedRequests.For(new string('9', 5000));

        big.Length.Should().BeGreaterThan(FacebookSignedRequest.MaxLength);
        FacebookSignedRequest.UserIdOf(big, SignedRequests.Secret).Should().BeNull();
    }

    private static long Seconds(TimeSpan fromNow) => DateTimeOffset.UtcNow.Add(fromNow).ToUnixTimeSeconds();

    [Theory]
    [InlineData(-25 * 3600)]
    [InlineData(-30 * 86400)]
    public void A_request_issued_more_than_a_day_ago_yields_nothing(int offsetSeconds)
    {
        var old = SignedRequests.For("1234567", issuedAt: Seconds(TimeSpan.FromSeconds(offsetSeconds)));

        FacebookSignedRequest.UserIdOf(old, SignedRequests.Secret).Should().BeNull();
        FacebookSignedRequest.UserIdOf(SignedRequests.For("1234567", issuedAt: Seconds(TimeSpan.FromHours(-23))), SignedRequests.Secret)
            .Should().Be("1234567", "a request inside the day is fine");
    }

    [Fact]
    public void A_request_issued_more_than_five_minutes_ahead_yields_nothing_and_a_little_skew_is_fine()
    {
        FacebookSignedRequest.UserIdOf(SignedRequests.For("1234567", issuedAt: Seconds(TimeSpan.FromMinutes(10))), SignedRequests.Secret)
            .Should().BeNull();
        FacebookSignedRequest.UserIdOf(SignedRequests.For("1234567", issuedAt: Seconds(TimeSpan.FromMinutes(2))), SignedRequests.Secret)
            .Should().Be("1234567");
    }

    [Fact]
    public void The_check_uses_the_clock_it_is_given()
    {
        var issued = DateTimeOffset.UtcNow.AddDays(-3);
        var request = SignedRequests.For("1234567", issuedAt: issued.ToUnixTimeSeconds());

        FacebookSignedRequest.UserIdOf(request, SignedRequests.Secret).Should().BeNull();
        FacebookSignedRequest.UserIdOf(request, SignedRequests.Secret, issued.AddHours(1)).Should().Be("1234567");
    }

    [Fact]
    public void A_signed_request_without_issued_at_is_still_accepted()
    {
        FacebookSignedRequest.UserIdOf(
            SignedRequests.Sign("""{"algorithm":"HMAC-SHA256","user_id":"1234567"}"""), SignedRequests.Secret).Should().Be("1234567");
    }

    [Theory]
    [InlineData("\"soon\"")]
    [InlineData("\"1700000000\"")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("1.5e300")]
    [InlineData("{}")]
    public void A_signed_request_with_an_issued_at_that_is_not_a_number_yields_nothing(string issuedAt)
    {
        var request = SignedRequests.Sign($$"""{"algorithm":"HMAC-SHA256","user_id":"1234567","issued_at":{{issuedAt}}}""");

        FacebookSignedRequest.UserIdOf(request, SignedRequests.Secret).Should().BeNull();
    }

    [Fact]
    public void No_configured_secret_yields_nothing()
    {
        FacebookSignedRequest.UserIdOf(SignedRequests.For("1234567", secret: ""), "").Should().BeNull();
        FacebookSignedRequest.UserIdOf(SignedRequests.For("1234567"), null).Should().BeNull();
    }
}

/// <summary>Facebook's data deletion callback and its status route, over real HTTP.</summary>
[Collection("Integration1")]
public class FacebookDataDeletionTests
{
    private const string BaseUrl = "https://cms.test.example";

    private static readonly Lock Gate = new();
    private static WebApplicationFactory<Program>? _host;
    private static int _ipCounter;

    private readonly IntegrationTestFixture _fixture;

    public FacebookDataDeletionTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        lock (Gate)
        {
            _host ??= fixture.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    { "App:BaseUrl", BaseUrl },
                    { "Facebook:AppId", "fb-app" },
                    { "Facebook:AppSecret", SignedRequests.Secret },
                })));
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static HttpClient Client()
    {
        var client = _host!.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, $"198.51.102.{Interlocked.Increment(ref _ipCounter) % 250 + 1}");
        return client;
    }

    private static Task<HttpResponseMessage> PostAsync(string? signedRequest) =>
        Client().PostAsync("/api/auth/facebook/data-deletion", new FormUrlEncodedContent(
            signedRequest is null ? [] : [new KeyValuePair<string, string>("signed_request", signedRequest)]), Ct);

    private async Task<ExternalIdentity> LinkAsync(string provider, string subject, Guid userId, DateTime? linkedAt = null)
    {
        var link = new ExternalIdentity
        {
            Id = ExternalIdentity.KeyOf(provider, subject), Issuer = provider, Subject = subject,
            UserId = userId, Provider = provider, LinkedAt = linkedAt ?? DateTime.UtcNow,
        };
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(link);
        await session.SaveChangesAsync(Ct);
        return link;
    }

    private async Task<bool> ExistsAsync(ExternalIdentity link)
    {
        using var scope = _fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IQuerySession>().LoadAsync<ExternalIdentity>(link.Id, Ct) is not null;
    }

    private static string NewFacebookId() => Random.Shared.NextInt64(1_000_000_000, long.MaxValue).ToString();

    [Fact]
    public async Task A_good_request_removes_only_that_users_facebook_link()
    {
        var mine = NewFacebookId();
        var person = Guid.NewGuid();
        var theirFacebook = await LinkAsync(ExternalProviders.Facebook, mine, person);
        var theirGoogle = await LinkAsync(ExternalProviders.Google, mine, person);
        var someoneElse = await LinkAsync(ExternalProviders.Facebook, NewFacebookId(), Guid.NewGuid());

        var response = await PostAsync(SignedRequests.For(mine));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsyncElement();
        var code = body.GetProperty("confirmation_code").GetString();
        code.Should().NotBeNullOrEmpty();
        body.GetProperty("url").GetString().Should().Be($"{BaseUrl}/api/auth/facebook/data-deletion/status/{code}");
        (await ExistsAsync(theirFacebook)).Should().BeFalse("that person's Facebook link is gone");
        (await ExistsAsync(theirGoogle)).Should().BeTrue("a link at another provider with the same id is not theirs to remove here");
        (await ExistsAsync(someoneElse)).Should().BeTrue("another person's link stays");
    }

    [Theory]
    [InlineData("wrong-signature")]
    [InlineData("wrong-algorithm")]
    [InlineData("bad-base64")]
    [InlineData("oversized")]
    [InlineData("missing")]
    public async Task A_bad_request_answers_400_and_removes_nothing(string kind)
    {
        var mine = NewFacebookId();
        var link = await LinkAsync(ExternalProviders.Facebook, mine, Guid.NewGuid());
        var input = kind switch
        {
            "wrong-signature" => SignedRequests.For(mine, secret: "not-the-secret"),
            "wrong-algorithm" => SignedRequests.For(mine, algorithm: "HMAC-SHA1"),
            "bad-base64" => "@@@@.####",
            "oversized" => SignedRequests.For(new string('7', 6000)),
            _ => null,
        };

        var response = await PostAsync(input);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ExistsAsync(link)).Should().BeTrue("a request that does not verify removes nothing");
        (await PostAsync(SignedRequests.For(mine))).StatusCode.Should().Be(HttpStatusCode.OK, "the same user with a good request is removed");
        (await ExistsAsync(link)).Should().BeFalse();
    }

    [Fact]
    public async Task A_request_issued_before_the_link_was_made_changes_nothing_and_one_issued_after_removes_it()
    {
        var mine = NewFacebookId();
        var person = Guid.NewGuid();
        // The person linked Facebook again an hour ago.
        var link = await LinkAsync(ExternalProviders.Facebook, mine, person, DateTime.UtcNow.AddHours(-1));
        var profile = new SocialProfile { Id = Guid.NewGuid(), UserId = person, Birthday = "01/02/1990", Source = "facebook" };
        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(profile);
            await session.SaveChangesAsync(Ct);
        }

        var stale = await PostAsync(SignedRequests.For(mine, issuedAt: DateTimeOffset.UtcNow.AddHours(-3).ToUnixTimeSeconds()));

        stale.StatusCode.Should().Be(HttpStatusCode.OK, "it answers as usual");
        (await ExistsAsync(link)).Should().BeTrue("the request predates this link");
        using (var scope = _fixture.Services.CreateScope())
        {
            (await scope.ServiceProvider.GetRequiredService<IQuerySession>().LoadAsync<SocialProfile>(profile.Id, Ct))
                .Should().NotBeNull("the profile is left alone too");
        }

        var code = (await stale.Content.ReadFromJsonAsyncElement()).GetProperty("confirmation_code").GetString();
        (await Client().GetAsync($"/api/auth/facebook/data-deletion/status/{code}", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        var fresh = await PostAsync(SignedRequests.For(mine, issuedAt: DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds()));

        fresh.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ExistsAsync(link)).Should().BeFalse("a request issued after the link removes it");
    }

    [Fact]
    public async Task A_correctly_signed_request_that_is_two_days_old_answers_400_and_removes_nothing()
    {
        var mine = NewFacebookId();
        var link = await LinkAsync(ExternalProviders.Facebook, mine, Guid.NewGuid());

        var replay = await PostAsync(SignedRequests.For(mine, issuedAt: DateTimeOffset.UtcNow.AddDays(-2).ToUnixTimeSeconds()));

        replay.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ExistsAsync(link)).Should().BeTrue();
        (await PostAsync(SignedRequests.For(mine))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await ExistsAsync(link)).Should().BeFalse();
    }

    [Fact]
    public async Task The_status_route_reports_a_request_it_answered_and_404s_for_any_other_code()
    {
        var response = await PostAsync(SignedRequests.For(NewFacebookId()));
        var code = (await response.Content.ReadFromJsonAsyncElement()).GetProperty("confirmation_code").GetString();

        var status = await Client().GetAsync($"/api/auth/facebook/data-deletion/status/{code}", Ct);
        var unknown = await Client().GetAsync($"/api/auth/facebook/data-deletion/status/{Guid.NewGuid():N}", Ct);

        status.StatusCode.Should().Be(HttpStatusCode.OK);
        (await status.Content.ReadFromJsonAsyncElement()).GetProperty("status").GetString().Should().Be("completed");
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}

internal static class HttpContentJsonExtensions
{
    public static async Task<JsonElement> ReadFromJsonAsyncElement(this HttpContent content) =>
        JsonDocument.Parse(await content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement.Clone();
}
