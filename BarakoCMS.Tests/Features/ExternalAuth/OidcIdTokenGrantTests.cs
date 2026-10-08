using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using BarakoCMS.ExternalAuth;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Features.ExternalAuth;

/// <summary>
/// POST /api/auth/oidc/{name}/id-token: a native app exchanges a provider's id token for a barako
/// token, against a stub issuer (#786).
/// </summary>
/// <remarks>
/// Every refusal is paired with a grant that succeeds on the same host, since a route that refused
/// everything would pass each refusal on its own. Each test uses its own nonces, subjects and
/// addresses, and its own client address for the rate limit.
/// </remarks>
[Collection("Sequential")]
public class OidcIdTokenGrantTests
{
    private const string IosClient = "com.example.ios";
    private const string AndroidClient = "1234-android.apps.example";

    private static readonly Lock Gate = new();
    private static WebApplicationFactory<Program>? _host;
    private static OidcStubProvider? _stub;
    private static int _ipCounter;

    private readonly IntegrationTestFixture _fixture;
    private readonly HttpClient _client;

    public OidcIdTokenGrantTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        lock (Gate)
        {
            if (_host is null)
            {
                var stub = new OidcStubProvider();
                var clients = new RecordingClientFactory(stub);
                _host = fixture.WithWebHostBuilder(b =>
                {
                    b.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(Settings()));
                    b.ConfigureServices(s =>
                    {
                        s.AddSingleton<IHttpClientFactory>(clients);
                        s.AddSingleton<OidcGrantSignIn>(SignIn);
                    });
                });
                _stub = stub;
            }
        }

        _client = ClientOf(_host);
    }

    private static FailingSignIn SignIn { get; } = new();

    private static OidcStubProvider Stub => _stub!;

    private static Dictionary<string, string?> Settings() => new()
    {
        { "App:BaseUrl", "https://cms.test.example" },
        { "Oidc:Providers:native:Authority", OidcStubProvider.Authority },
        { "Oidc:Providers:native:ClientId", OidcStubProvider.ClientId },
        { "Oidc:Providers:native:ClientSecret", OidcStubProvider.ClientSecret },
        { "Oidc:Providers:native:IdTokenAudiences:0", IosClient },
        { "Oidc:Providers:native:IdTokenAudiences:1", AndroidClient },
        { "Oidc:Providers:native:IdTokenAudiences:2", OidcStubProvider.ClientId },
        { "Oidc:Providers:webonly:Authority", OidcStubProvider.Authority },
        { "Oidc:Providers:webonly:ClientId", OidcStubProvider.ClientId },
        { "Oidc:Providers:webonly:ClientSecret", OidcStubProvider.ClientSecret },
        { "GitHub:ClientId", "" },
        { "Google:ClientId", "" },
        { "Facebook:AppId", "" },
        { "LinkedIn:ClientId", "" },
    };

    private static HttpClient ClientOf(WebApplicationFactory<Program> host)
    {
        var client = host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, $"192.0.2.{Interlocked.Increment(ref _ipCounter) % 250 + 1}");
        return client;
    }

    private static string NewNonce() => OidcFlow.New().Nonce;

    private static string NewSubject() => "sub-" + Guid.NewGuid().ToString("N");

    private static string NewEmail() => $"grant-{Guid.NewGuid():n}@example.com";

    private static string Token(string nonce, string subject, string email, Action<Dictionary<string, object?>>? edit = null, RSA? key = null)
    {
        var claims = OidcTestTokens.Claims(nonce, subject, email);
        claims["aud"] = IosClient;
        edit?.Invoke(claims);
        return OidcTestTokens.Rs256(key ?? Stub.Key, claims);
    }

    private Task<HttpResponseMessage> GrantAsync(
        string? idToken, string? nonce, string provider = "native", HttpClient? client = null, string? club = null) =>
        (client ?? _client).PostAsJsonAsync(
            $"/api/auth/oidc/{provider}/id-token",
            new Dictionary<string, string?> { ["idToken"] = idToken, ["nonce"] = nonce, ["club"] = club },
            TestContext.Current.CancellationToken);

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return document.RootElement.Clone();
    }

    private async Task<barakoCMS.Models.User?> UserByEmailAsync(string email)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return await session.Query<barakoCMS.Models.User>()
            .FirstOrDefaultAsync(u => u.Email == email, TestContext.Current.CancellationToken);
    }

    private static async Task ShouldBeSignedInAsAsync(HttpResponseMessage response, barakoCMS.Models.User? user)
    {
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await BodyAsync(response);
        body.GetProperty("refreshToken").GetString().Should().NotBeNullOrEmpty();
        user.Should().NotBeNull();
        new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(body.GetProperty("token").GetString())
            .Claims.Should().Contain(c => c.Type == "UserId" && c.Value == user!.Id.ToString());
    }

    [Theory]
    [InlineData(IosClient)]
    [InlineData(AndroidClient)]
    public async Task The_grant_signs_in_with_each_configured_audience(string audience)
    {
        var email = NewEmail();
        var subject = NewSubject();
        var nonce = NewNonce();

        var response = await GrantAsync(Token(nonce, subject, email, c => c["aud"] = audience), nonce);

        await ShouldBeSignedInAsAsync(response, await UserByEmailAsync(email));
        using var scope = _fixture.Services.CreateScope();
        var link = await scope.ServiceProvider.GetRequiredService<IQuerySession>()
            .LoadAsync<ExternalIdentity>(ExternalIdentity.KeyOf(OidcStubProvider.Authority, subject), TestContext.Current.CancellationToken);
        link.Should().NotBeNull("the grant links by issuer and subject, as the callback does");
        link!.Provider.Should().Be("native");
    }

    [Fact]
    public async Task A_google_native_token_naming_the_web_client_as_aud_and_a_listed_app_as_azp_signs_in()
    {
        var email = NewEmail();
        var nonce = NewNonce();
        var token = Token(nonce, NewSubject(), email, c =>
        {
            c["aud"] = OidcStubProvider.ClientId;
            c["azp"] = AndroidClient;
        });

        await ShouldBeSignedInAsAsync(await GrantAsync(token, nonce), await UserByEmailAsync(email));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_token_the_web_client_was_issued_is_not_taken_by_the_grant(bool azpIsTheWebClient)
    {
        var email = NewEmail();
        var nonce = NewNonce();
        var token = Token(nonce, NewSubject(), email, c =>
        {
            c["aud"] = OidcStubProvider.ClientId;
            if (azpIsTheWebClient)
            {
                c["azp"] = OidcStubProvider.ClientId;
            }
        });

        (await GrantAsync(token, nonce)).StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "the web client is listed for Google's native shape, which names an app as azp; a browser token names none");
        (await UserByEmailAsync(email)).Should().BeNull();

        var fine = NewNonce();
        var control = NewEmail();
        await ShouldBeSignedInAsAsync(
            await GrantAsync(Token(fine, NewSubject(), control, c => { c["aud"] = OidcStubProvider.ClientId; c["azp"] = IosClient; }), fine),
            await UserByEmailAsync(control));
    }

    [Fact]
    public async Task A_token_with_several_audiences_needs_a_listed_azp()
    {
        var email = NewEmail();
        var nonce = NewNonce();
        var token = Token(nonce, NewSubject(), email, c =>
        {
            c["aud"] = new[] { IosClient, AndroidClient };
            c["azp"] = AndroidClient;
        });

        await ShouldBeSignedInAsAsync(await GrantAsync(token, nonce), await UserByEmailAsync(email));

        var otherNonce = NewNonce();
        var unlisted = Token(otherNonce, NewSubject(), NewEmail(), c =>
        {
            c["aud"] = new[] { IosClient, AndroidClient };
            c["azp"] = "someone-else";
        });
        (await GrantAsync(unlisted, otherNonce)).StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "with several audiences the party it was issued to has to be one of ours too");
    }

    public enum Flaw
    {
        AudienceNotListed,
        WebClientAudience,
        SignedByAnotherKey,
        Expired,
        EmailNotVerified,
        NoNonceInToken,
        AnotherNonceInToken,
        WrongIssuer,
    }

    [Theory]
    [InlineData(Flaw.AudienceNotListed)]
    [InlineData(Flaw.WebClientAudience)]
    [InlineData(Flaw.SignedByAnotherKey)]
    [InlineData(Flaw.Expired)]
    [InlineData(Flaw.EmailNotVerified)]
    [InlineData(Flaw.NoNonceInToken)]
    [InlineData(Flaw.AnotherNonceInToken)]
    [InlineData(Flaw.WrongIssuer)]
    public async Task A_token_with_one_thing_wrong_signs_nobody_in(Flaw flaw)
    {
        var email = NewEmail();
        var nonce = NewNonce();
        using var otherKey = RSA.Create(2048);

        var token = Token(nonce, NewSubject(), email, c =>
        {
            switch (flaw)
            {
                case Flaw.AudienceNotListed: c["aud"] = "some-other-app"; break;
                case Flaw.WebClientAudience: c["aud"] = OidcStubProvider.ClientId; break;
                case Flaw.Expired: c["exp"] = DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeSeconds(); break;
                case Flaw.EmailNotVerified: c["email_verified"] = false; break;
                case Flaw.NoNonceInToken: c.Remove("nonce"); break;
                case Flaw.AnotherNonceInToken: c["nonce"] = NewNonce(); break;
                case Flaw.WrongIssuer: c["iss"] = OidcStubProvider.Authority + "/"; break;
            }
        }, flaw == Flaw.SignedByAnotherKey ? otherKey : null);

        var response = await GrantAsync(token, nonce);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, $"{flaw} is not a token this provider issued to one of our apps");
        (await UserByEmailAsync(email)).Should().BeNull("a refused token creates no account");

        var fine = NewNonce();
        var control = NewEmail();
        await ShouldBeSignedInAsAsync(await GrantAsync(Token(fine, NewSubject(), control), fine), await UserByEmailAsync(control));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("has a space in the middle of it")]
    public async Task A_request_without_a_usable_nonce_is_refused(string? nonce)
    {
        var email = NewEmail();
        var token = Token(nonce ?? NewNonce(), NewSubject(), email);

        var response = await GrantAsync(token, nonce);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, "the nonce is required, and is what makes a token usable once");
        (await UserByEmailAsync(email)).Should().BeNull();
    }

    [Fact]
    public async Task A_nonce_works_once_whatever_token_carries_it()
    {
        var email = NewEmail();
        var subject = NewSubject();
        var nonce = NewNonce();
        var token = Token(nonce, subject, email);

        await ShouldBeSignedInAsAsync(await GrantAsync(token, nonce), await UserByEmailAsync(email));

        var replay = await GrantAsync(token, nonce);
        replay.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the same token again is a replay");
        (await replay.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Contain("already used");

        var another = NewEmail();
        var fresh = await GrantAsync(Token(nonce, NewSubject(), another), nonce, client: ClientOf(_host!));
        fresh.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "a second token with a spent nonce is refused too, from any client");
        (await UserByEmailAsync(another)).Should().BeNull();

        using var scope = _fixture.Services.CreateScope();
        var used = await scope.ServiceProvider.GetRequiredService<IQuerySession>()
            .LoadAsync<OidcUsedNonce>(OidcUsedNonce.KeyOf(OidcStubProvider.Authority, nonce), TestContext.Current.CancellationToken);
        used.Should().NotBeNull();
        used!.ExpiresAt.Should().BeAfter(DateTime.UtcNow, "the nonce is kept until the token it came in has expired");
        used.ExpiresAt.Should().BeBefore(DateTime.UtcNow.AddMinutes(7));
    }

    [Fact]
    public async Task Two_grants_racing_with_one_token_sign_in_once()
    {
        var email = NewEmail();
        var nonce = NewNonce();
        var token = Token(nonce, NewSubject(), email);

        // The first grant makes the user, so the race is between two grants for a known account.
        var warm = NewNonce();
        await ShouldBeSignedInAsAsync(await GrantAsync(Token(warm, NewSubject(), email), warm), await UserByEmailAsync(email));

        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => GrantAsync(token, nonce, client: ClientOf(_host!))));

        responses.Should().HaveCount(4);
        responses.Count(r => r.StatusCode == HttpStatusCode.OK).Should().Be(1, "the unique insert lets one of them through");
        responses.Count(r => r.StatusCode == HttpStatusCode.Unauthorized).Should().Be(3);
    }

    [Fact]
    public async Task A_token_that_lives_longer_than_a_day_is_refused()
    {
        var email = NewEmail();
        var nonce = NewNonce();
        var token = Token(nonce, NewSubject(), email, c => c["exp"] = DateTimeOffset.UtcNow.AddDays(2).ToUnixTimeSeconds());

        (await GrantAsync(token, nonce)).StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "how long a used nonce is kept is bounded by how long a token may live");
        (await UserByEmailAsync(email)).Should().BeNull();
    }

    [Theory]
    [InlineData("webonly")]
    [InlineData("nobody")]
    public async Task The_grant_is_off_for_a_provider_with_no_audiences_listed(string provider)
    {
        var email = NewEmail();
        var nonce = NewNonce();

        (await GrantAsync(Token(nonce, NewSubject(), email, c => c["aud"] = OidcStubProvider.ClientId), nonce, provider))
            .StatusCode.Should().Be(HttpStatusCode.NotFound, "a provider configured for the redirect flow is not opened to the grant");
        (await UserByEmailAsync(email)).Should().BeNull();
    }

    [Fact]
    public async Task The_grant_draws_on_the_oidc_rate_limit()
    {
        var client = ClientOf(_host!);
        for (var i = 0; i < OidcSupport.DefaultPermitLimit; i++)
        {
            (await GrantAsync("not-a-token", NewNonce(), client: client)).StatusCode
                .Should().Be(HttpStatusCode.Unauthorized, "request {0} is inside the limit", i + 1);
        }

        (await GrantAsync("not-a-token", NewNonce(), client: client)).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    /// <summary>
    /// A sign-in whose final commit fails the way a lost first sign-in race does (a unique
    /// violation) is told to try again, and its own token has to work then: the nonce went down with
    /// the failed commit. With a device id the DeviceTrust module commits the user and link earlier,
    /// which must not spend the nonce either.
    /// </summary>
    /// <remarks>
    /// The failure is made, not waited for. A real race between two first sign-ins happens only when
    /// the timing allows, so a test that needed one would pass or fail on the scheduler.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_sign_in_whose_commit_loses_a_race_is_answered_409_and_its_token_works_on_retry(bool withDevice)
    {
        var bystander = new barakoCMS.Models.User
        {
            Id = Guid.NewGuid(),
            Email = NewEmail(),
            Username = $"grant-{Guid.NewGuid():n}",
            PasswordHash = "",
        };
        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(bystander);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var client = ClientOf(_host!);
        if (withDevice)
        {
            client.DefaultRequestHeaders.Add(
                barakoCMS.Infrastructure.DeviceContext.DeviceIdHeader, "device-" + Guid.NewGuid().ToString("N"));
        }

        var email = NewEmail();
        var nonce = NewNonce();
        var token = Token(nonce, NewSubject(), email);
        SignIn.FailNextFinalCommitWith(bystander);

        var lost = await GrantAsync(token, nonce, client: client);

        lost.StatusCode.Should().Be(HttpStatusCode.Conflict, "the final commit failed on a unique violation that was not the nonce");
        SignIn.Armed.Should().BeFalse("the failure was made on this grant's final commit");
        using (var scope = _fixture.Services.CreateScope())
        {
            (await scope.ServiceProvider.GetRequiredService<IQuerySession>()
                .LoadAsync<OidcUsedNonce>(OidcUsedNonce.KeyOf(OidcStubProvider.Authority, nonce), TestContext.Current.CancellationToken))
                .Should().BeNull("the nonce is spent only with the outcome");
        }

        await ShouldBeSignedInAsAsync(await GrantAsync(token, nonce, client: client), await UserByEmailAsync(email));
        (await GrantAsync(token, nonce, client: client)).StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "once it has signed in, the token is spent");
    }

    /// <summary>
    /// The real sign-in, except that when armed it adds to the next final commit an insert of a user
    /// that is already stored, so that commit fails on a unique violation the way a lost race does.
    /// </summary>
    internal sealed class FailingSignIn : OidcGrantSignIn
    {
        private barakoCMS.Models.User? _conflict;

        public bool Armed => Volatile.Read(ref _conflict) is not null;

        public void FailNextFinalCommitWith(barakoCMS.Models.User existing) => Volatile.Write(ref _conflict, existing);

        public override Task<(SocialSignIn.Tokens Tokens, bool EmailNotVerified)> IssueAsync(
            IDocumentSession session,
            IConfiguration config,
            barakoCMS.Core.Interfaces.IDeviceGate deviceGate,
            barakoCMS.Infrastructure.Auth.ITokenIssuer tokenIssuer,
            barakoCMS.Infrastructure.Auth.Mfa.IMfaService mfa,
            Microsoft.AspNetCore.Http.HttpContext http,
            OidcIdentity identity,
            string provider,
            string club,
            CancellationToken ct,
            Action<IDocumentSession> beforeFinalCommit) =>
            base.IssueAsync(session, config, deviceGate, tokenIssuer, mfa, http, identity, provider, club, ct, s =>
            {
                beforeFinalCommit(s);
                if (Interlocked.Exchange(ref _conflict, null) is { } existing)
                {
                    s.Insert(new barakoCMS.Models.User
                    {
                        Id = existing.Id,
                        Email = existing.Email,
                        Username = existing.Username,
                        PasswordHash = "",
                    });
                }
            });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_nonce_works_again_once_its_token_has_expired(bool purged)
    {
        var nonce = NewNonce();
        var first = NewEmail();
        await ShouldBeSignedInAsAsync(await GrantAsync(Token(nonce, NewSubject(), first), nonce), await UserByEmailAsync(first));

        var key = OidcUsedNonce.KeyOf(OidcStubProvider.Authority, nonce);
        var store = _host!.Services.GetRequiredService<IDocumentStore>();
        await using (var session = store.LightweightSession())
        {
            session.Store(new OidcUsedNonce { Id = key, ExpiresAt = DateTime.UtcNow.AddMinutes(-1) });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        if (purged)
        {
            (await _host.Services.GetRequiredService<OidcUsedNonces>().PurgeAsync(store, TestContext.Current.CancellationToken))
                .Should().BeGreaterThan(0);
            await using var query = store.QuerySession();
            (await query.LoadAsync<OidcUsedNonce>(key, TestContext.Current.CancellationToken)).Should().BeNull("the purge takes expired records");
        }

        var second = NewEmail();
        await ShouldBeSignedInAsAsync(await GrantAsync(Token(nonce, NewSubject(), second), nonce), await UserByEmailAsync(second));
    }

    [Fact]
    public async Task The_purge_takes_only_expired_records()
    {
        var store = _host!.Services.GetRequiredService<IDocumentStore>();
        var expired = Enumerable.Range(0, 3).Select(_ => OidcUsedNonce.KeyOf(OidcStubProvider.Authority, NewNonce())).ToList();
        var live = OidcUsedNonce.KeyOf(OidcStubProvider.Authority, NewNonce());
        await using (var session = store.LightweightSession())
        {
            foreach (var id in expired)
            {
                session.Store(new OidcUsedNonce { Id = id, ExpiresAt = DateTime.UtcNow.AddMinutes(-5) });
            }

            session.Store(new OidcUsedNonce { Id = live, ExpiresAt = DateTime.UtcNow.AddMinutes(5) });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        (await _host.Services.GetRequiredService<OidcUsedNonces>().PurgeAsync(store, TestContext.Current.CancellationToken))
            .Should().BeGreaterThanOrEqualTo(3);

        await using var query = store.QuerySession();
        var left = await query.LoadManyAsync<OidcUsedNonce>(TestContext.Current.CancellationToken, [.. expired, live]);
        left.Should().HaveCount(1);
        left[0].Id.Should().Be(live);
    }

    [Fact]
    public async Task A_club_the_person_does_not_belong_to_is_answered_403()
    {
        string slug;
        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            slug = $"club-{Guid.NewGuid():N}".ToLowerInvariant();
            session.Store(new barakoCMS.Models.Tenant { Id = Guid.NewGuid(), Slug = slug, Name = slug, IsActive = true });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var nonce = NewNonce();
        var response = await GrantAsync(Token(nonce, NewSubject(), NewEmail()), nonce, club: slug);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, "proving an identity is not membership of a club");
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().NotContain("refreshToken\":\"");
    }
}
