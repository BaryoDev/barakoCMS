using BarakoCMS.ExternalAuth;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Features.ExternalAuth;

/// <summary>
/// A provider proving an email address is one factor. An account that enrolled a second one must be
/// asked for it here, exactly as a password login is.
/// </summary>
/// <remarks>
/// This is the regression named in #120. Version 0.1.5 of the module minted a session token straight
/// from the provider callback without ever asking whether the account had MFA enrolled, so taking
/// over somebody's Google or GitHub account walked past the second factor. The fix landed in source
/// and then sat unpublished for a release, because the module version was not bumped and the release
/// skips a package whose version has not moved. Nothing failed. It quietly kept shipping the bypass.
///
/// Driven through <see cref="SocialSignIn.IssueAsync"/> rather than a provider callback because that
/// is the one place all four providers converge on, so a fifth provider added later inherits this
/// without anybody having to remember.
/// </remarks>
[Collection("Sequential")]
public class SocialSignInMfaTests
{
    private readonly IntegrationTestFixture _fixture;

    public SocialSignInMfaTests(IntegrationTestFixture fixture) => _fixture = fixture;

    /// <summary>
    /// The regression itself. An enrolled account gets a challenge and no credential of any kind.
    /// </summary>
    [Fact]
    public async Task An_account_with_mfa_enrolled_is_challenged_rather_than_signed_in()
    {
        var user = await CreateUserAsync(mfaEnrolled: true);

        var tokens = await IssueAsync(user.Email);

        tokens.RequiresMfa.Should().BeTrue(
            "a provider proving the email is the first factor, not both of them");
        tokens.Allowed.Should().BeFalse();
        tokens.Token.Should().BeEmpty("an access token here is the second factor skipped");
        tokens.Refresh.Should().BeEmpty(
            "a refresh token is worse than an access token, because it outlives the window");
    }

    /// <summary>
    /// The challenge is the one /api/auth/mfa/verify accepts, and it names this user.
    /// </summary>
    /// <remarks>
    /// Asserting only that some string came back would pass on a challenge nothing can complete,
    /// which locks an enrolled user out of social sign-in rather than protecting them, and would
    /// also pass on a challenge minted for the wrong user.
    /// </remarks>
    [Fact]
    public async Task The_challenge_completes_the_second_factor_for_that_same_user()
    {
        var user = await CreateUserAsync(mfaEnrolled: true);

        var tokens = await IssueAsync(user.Email);

        tokens.MfaChallenge.Should().NotBeNullOrEmpty();

        using var scope = _fixture.Services.CreateScope();
        var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        barakoCMS.Infrastructure.Auth.Mfa.MfaChallengeToken
            .ValidatedUserId(config, tokens.MfaChallenge!)
            .Should().Be(user.Id, "the pending second step is bound to the account it was raised for");
    }

    /// <summary>
    /// The positive control. Refusing every social sign-in would satisfy both tests above and would
    /// be a worse outcome than the bug.
    /// </summary>
    [Fact]
    public async Task An_account_without_mfa_still_receives_its_tokens()
    {
        var user = await CreateUserAsync(mfaEnrolled: false);

        var tokens = await IssueAsync(user.Email);

        tokens.RequiresMfa.Should().BeFalse();
        tokens.Allowed.Should().BeTrue("this is the case the flow exists for");
        tokens.Token.Should().NotBeEmpty();
        tokens.Refresh.Should().NotBeEmpty();
    }

    /// <summary>
    /// The token a provider sign-in issues carries the same claims a password login's does, and is
    /// scoped to the tenant that was asked for rather than to whatever the provider knew about.
    /// </summary>
    [Fact]
    public async Task The_issued_token_names_the_user_and_the_tenant_it_was_asked_for()
    {
        var user = await CreateUserAsync(mfaEnrolled: false);

        var tokens = await IssueAsync(user.Email);

        var jwt = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(tokens.Token);
        jwt.Claims.Should().Contain(c => c.Type == "UserId" && c.Value == user.Id.ToString());
        jwt.Claims.Should().Contain(c => c.Type == "Username" && c.Value == user.Username);
        jwt.Claims.Should().Contain(c => c.Type == "tenant" && c.Value == barakoCMS.Models.Tenant.DefaultSlug,
            "which club the token is good for is decided here, not by the provider");
        jwt.Claims.Should().Contain(c => c.Type == "jti",
            "revocation works by token id, so a token without one cannot be revoked");
    }

    private async Task<barakoCMS.Models.User> CreateUserAsync(bool mfaEnrolled)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var handle = $"social-mfa-{Guid.NewGuid():n}";
        var user = new barakoCMS.Models.User
        {
            Id = Guid.NewGuid(),
            Email = $"{handle}@example.com",
            Username = handle,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("a-real-password"),
        };
        session.Store(user);

        if (mfaEnrolled)
        {
            var protector = scope.ServiceProvider
                .GetRequiredService<barakoCMS.Infrastructure.Auth.Mfa.IMfaSecretProtector>();
            session.Store(new barakoCMS.Models.MfaSecret
            {
                Id = user.Id,
                EncryptedSecret = protector.Protect(OtpNet.Base32Encoding.ToString(
                    OtpNet.KeyGeneration.GenerateRandomKey(20))),
                Enabled = true,
                ConfirmedAt = DateTime.UtcNow,
            });
        }

        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return user;
    }

    private async Task<SocialSignIn.Tokens> IssueAsync(string email)
    {
        using var scope = _fixture.Services.CreateScope();
        var provider = scope.ServiceProvider;

        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Request.Headers["User-Agent"] = "social-sign-in-mfa-tests";

        return await SocialSignIn.IssueAsync(
            provider.GetRequiredService<IDocumentSession>(),
            provider.GetRequiredService<IConfiguration>(),
            provider.GetRequiredService<barakoCMS.Core.Interfaces.IDeviceGate>(),
            provider.GetRequiredService<barakoCMS.Infrastructure.Auth.ITokenIssuer>(),
            provider.GetRequiredService<barakoCMS.Infrastructure.Auth.Mfa.IMfaService>(),
            context,
            email,
            emailVerified: true,
            barakoCMS.Models.Tenant.DefaultSlug,
            TestContext.Current.CancellationToken);
    }
}
