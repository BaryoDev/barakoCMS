using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests;

/// <summary>
/// An access token issued before a user's sessions were invalidated is refused.
/// </summary>
/// <remarks>
/// Revoking refresh tokens stopped a session being renewed and did nothing to an access token
/// already issued, which stays valid for up to fifteen minutes. So a password change, a reset or
/// enabling MFA all left a stolen session working for the rest of that window. That was documented
/// in CHANGELOG 3.18.0 rather than hidden, and #82 is closing it.
///
/// The two failure modes worth testing are opposite to each other, and the second is the dangerous
/// one. Under-enforcing leaves the window open. Over-enforcing locks out every user at once, which
/// is why a user who has never had a security event must never be checked against anything, and why
/// a token minted moments after the bump has to survive.
/// </remarks>
[Collection("Sequential")]
public class SessionEpochTests
{
    private readonly IntegrationTestFixture _factory;

    public SessionEpochTests(IntegrationTestFixture factory) => _factory = factory;

    private async Task<(Guid Id, string Username)> SeedUserAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var role = await session.Query<Role>().FirstOrDefaultAsync(r => r.Name == "SuperAdmin");
        if (role is null) { role = new Role { Id = Guid.NewGuid(), Name = "SuperAdmin" }; session.Store(role); }

        var id = Guid.NewGuid();
        session.Store(new User
        {
            Id = id,
            Username = $"epoch_{id:n}",
            Email = $"epoch_{id:n}@example.com",
            RoleIds = [role.Id],
        });
        await session.SaveChangesAsync();
        return (id, $"epoch_{id:n}");
    }

    private HttpClient ClientFor(Guid userId)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(roles: ["SuperAdmin"], userId: userId.ToString()));
        return client;
    }

    private async Task SetEpochAsync(Guid userId, DateTime validFrom)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var user = await session.LoadAsync<User>(userId);
        user!.TokensValidFrom = validFrom;
        session.Store(user);
        await session.SaveChangesAsync();

        // The service caches, and this test wrote to the database behind it. Dropping the entry is
        // what a real bump does through RevokeRefreshTokens, so the test is not relying on a cache
        // that happens to be cold.
        scope.ServiceProvider.GetRequiredService<barakoCMS.Infrastructure.Services.ISessionEpochService>()
            .Invalidate(userId);
    }


    /// <summary>
    /// A token from the real issuer, minted through DI rather than through the login endpoint.
    /// </summary>
    /// <remarks>
    /// `/api/auth/*` allows five requests per fifteen minutes per IP, shared between login and
    /// refresh, so a test that logs in over HTTP passes alone and fails in the full suite once other
    /// tests have spent the budget. Both of these tests did exactly that.
    ///
    /// Going through ITokenIssuer is also the more precise test. What is being asserted is what the
    /// issuer mints, not what the login endpoint does with it, and the endpoint is covered elsewhere.
    /// </remarks>
    private async Task<string> RealTokenAsync(Guid userId)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var issuer = scope.ServiceProvider.GetRequiredService<barakoCMS.Infrastructure.Auth.ITokenIssuer>();

        var user = await session.LoadAsync<User>(userId);
        var result = await issuer.IssueAccessTokenAsync(user!, "default");

        result.Allowed.Should().BeTrue("the seeded user must be able to hold a token for the default tenant");
        return result.Token;
    }

    /// <summary>
    /// The control, and the one that matters most.
    /// </summary>
    /// <remarks>
    /// Almost every user has no epoch. If the check treats a null as "invalidate everything", every
    /// user of the instance is locked out at once. Without this assertion, a middleware that refused
    /// unconditionally would satisfy every other test on this page.
    /// </remarks>
    [Fact]
    public async Task A_user_who_has_never_had_a_security_event_is_served()
    {
        var (id, _) = await SeedUserAsync();

        var response = await ClientFor(id).GetAsync("/api/contents");

        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized,
            "TokensValidFrom is null for this user, and null means nothing has ever invalidated their tokens");
    }

    [Fact]
    public async Task A_token_issued_before_the_epoch_is_refused()
    {
        var (id, _) = await SeedUserAsync();
        var client = ClientFor(id);

        // Issued now, invalidated a minute later. Well outside the skew allowance.
        await SetEpochAsync(id, DateTime.UtcNow.AddMinutes(1));

        var response = await client.GetAsync("/api/contents");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "the token predates the moment this user's sessions were invalidated");
    }

    [Fact]
    public async Task A_token_issued_after_the_epoch_is_served()
    {
        var (id, _) = await SeedUserAsync();

        await SetEpochAsync(id, DateTime.UtcNow.AddMinutes(-5));
        var client = ClientFor(id);

        var response = await client.GetAsync("/api/contents");

        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized,
            "signing in again after a password change has to work, or the fix is an outage");
    }

    /// <summary>
    /// A token minted in the same second as the bump survives.
    /// </summary>
    /// <remarks>
    /// `iat` is whole seconds and the epoch has sub-second precision, so a token issued 50ms after a
    /// bump at 12:00:00.900 carries iat 12:00:00, which is numerically less. Without truncating the
    /// epoch to seconds the middleware refuses a token minted after the event it should survive, and
    /// the symptom is a sign-in that silently does not work.
    /// </remarks>
    [Fact]
    public async Task A_token_minted_at_the_same_instant_as_the_bump_is_served()
    {
        var (id, _) = await SeedUserAsync();

        await SetEpochAsync(id, DateTime.UtcNow);
        var client = ClientFor(id);

        var response = await client.GetAsync("/api/contents");

        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized,
            "second precision on iat must not turn a fresh login into a refused one");
    }

    /// <summary>
    /// Changing a password ends the sessions that existed before it.
    /// </summary>
    /// <remarks>
    /// The end to end case, and the one the issue is actually about. The two halves are separate
    /// mechanisms, refresh revocation and the epoch, and this asserts the second because the first
    /// was already there and was not enough.
    /// </remarks>
    [Fact]
    public async Task Changing_a_password_refuses_the_access_token_that_was_already_issued()
    {
        var (id, username) = await SeedUserAsync();

        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var user = await session.LoadAsync<User>(id);
            user!.PasswordHash = BCrypt.Net.BCrypt.HashPassword("OldPassw0rd!");
            session.Store(user);
            await session.SaveChangesAsync();
        }

        var client = ClientFor(id);

        var before = await client.GetAsync("/api/contents");
        before.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized, "the session works to start with");

        // The token has to be from an earlier second than the bump, because same-second tokens are
        // served by design (see the middleware's remarks on iat precision). A second of real time,
        // in one test, to assert the case the whole issue is about.
        await Task.Delay(1100);

        var change = await client.PostAsJsonAsync("/api/me/password", new
        {
            currentPassword = "OldPassw0rd!",
            newPassword = "BrandNewPassw0rd!",
        });
        change.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}",
            change.StatusCode, await change.Content.ReadAsStringAsync());

        var after = await client.GetAsync("/api/contents");

        after.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "the access token issued before the password change kept working for up to fifteen "
          + "minutes, which is the window #82 exists to close");
    }

    /// <summary>
    /// A token minted by the real issuer carries an iat claim.
    /// </summary>
    /// <remarks>
    /// The guard that stops this whole feature becoming a no-op. The middleware serves the request
    /// when it cannot read an iat, deliberately, because refusing on a parse failure locks out every
    /// user at once. So a token without one is not an error anywhere: the epoch check simply never
    /// fires, every other test on this page still passes because they use the test fixture's token,
    /// and the control ships doing nothing.
    ///
    /// Neither TokenIssuer nor the fixture set iat before #82. Both do now, and this asserts the
    /// production one rather than the fixture, because the fixture is not what ships.
    /// </remarks>
    [Fact]
    public async Task A_token_from_the_real_issuer_carries_an_iat_claim()
    {
        var (id, _) = await SeedUserAsync();

        var jwt = await RealTokenAsync(id);

        var parsed = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(jwt);
        var iat = parsed.Claims.FirstOrDefault(c => c.Type == "iat")?.Value;

        iat.Should().NotBeNull(
            "without iat the session epoch check has nothing to compare against and serves every "
          + "request, so the control would ship doing nothing");
        long.TryParse(iat, out var seconds).Should().BeTrue("iat is seconds since the epoch");
        DateTimeOffset.FromUnixTimeSeconds(seconds).Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));
    }

    /// <summary>
    /// Logging out succeeds, and the token it revoked is refused afterwards.
    /// </summary>
    /// <remarks>
    /// There were no logout tests at all, and the endpoint was throwing. `AddMemoryCache` sets a
    /// `SizeLimit`, and an entry stored without a `Size` throws `InvalidOperationException`.
    /// `TokenRevocationService.RevokeTokenAsync` cached the revocation without one, outside any
    /// try, so every logout raised.
    ///
    /// Found while building the session epoch, whose own cache write threw for the same reason.
    /// There it was invisible: the middleware catches and serves, so the control shipped doing
    /// nothing while looking fine. Both now pass a `Size`, and this is the test that would have
    /// caught the older of the two.
    /// </remarks>
    [Fact]
    public async Task Logging_out_succeeds_and_the_token_is_refused_afterwards()
    {
        var (id, _) = await SeedUserAsync();

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", await RealTokenAsync(id));

        var logout = await client.PostAsync("/api/auth/logout", null);

        logout.IsSuccessStatusCode.Should().BeTrue(
            "logging out threw because the revocation cache entry carried no Size and the cache has "
          + "a SizeLimit. Got {0}: {1}", logout.StatusCode, await logout.Content.ReadAsStringAsync());

        var after = await client.GetAsync("/api/contents");
        after.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "the token was revoked by id, which is a separate mechanism from the session epoch");
    }
}
