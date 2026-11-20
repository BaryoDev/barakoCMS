using barakoCMS.Infrastructure.Auth.Mfa;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// <c>IMfaService.CreateChallengeToken</c> (#972): how a module that does not reference core
/// starts a second-factor challenge. Core signs the token, so the signing key stays in core.
/// </summary>
/// <remarks>
/// Signing goes through <c>JwtBearer.CreateToken</c>, which reads FastEndpoints' process-wide resolver.
/// A plain unit test has none, so the class joins the collection whose fixture host sets it.
/// </remarks>
[Collection("Integration1")]
public class MfaChallengeMintTests
{
    public MfaChallengeMintTests(IntegrationTestFixture factory) => _ = factory;

    private static readonly IConfiguration Config = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["JWT:Key"] = "test-super-secret-key-that-is-at-least-32-chars-long",
            ["JWT:Issuer"] = "tests",
            ["JWT:Audience"] = "tests",
        })
        .Build();

    private static IMfaService Core() =>
        new MfaService(Substitute.For<IDocumentSession>(), Substitute.For<IMfaSecretProtector>(), Config);

    /// <summary>An implementation compiled before the member existed: it declares none of it.</summary>
    private sealed class OlderImplementation : IMfaService
    {
        public Task<bool> IsEnabledAsync(Guid userId, CancellationToken ct) => Task.FromResult(false);
        public Task<(string Secret, string OtpauthUri)> BeginSetupAsync(barakoCMS.Models.User user, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>?> ConfirmSetupAsync(Guid userId, string code, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> VerifyCodeAsync(Guid userId, string code, CancellationToken ct) => Task.FromResult(false);
        public Task<bool> DisableAsync(Guid userId, string code, CancellationToken ct) => Task.FromResult(false);
    }

    [Fact]
    public void Core_mints_a_challenge_verify_accepts_for_that_user_and_first_factor()
    {
        var userId = Guid.NewGuid();

        var token = Core().CreateChallengeToken(userId, "social:google");

        var challenge = MfaChallengeToken.ValidatedChallenge(Config, token);
        challenge.Should().NotBeNull();
        challenge!.UserId.Should().Be(userId);
        challenge.FirstFactor.Should().Be("social:google");
        challenge.Grant.Should().BeNull("no roles were passed, so there is nothing to grant");
    }

    [Fact]
    public void Core_carries_the_roles_an_external_sign_in_earned()
    {
        var roles = new List<Guid> { Guid.NewGuid(), Guid.NewGuid() };

        var token = Core().CreateChallengeToken(Guid.NewGuid(), "social:github", "acme", "github", roles);

        var grant = MfaChallengeToken.ValidatedChallenge(Config, token)!.Grant;
        grant.Should().NotBeNull();
        grant!.TenantSlug.Should().Be("acme");
        grant.Source.Should().Be("github");
        grant.RoleIds.Should().Equal(roles);
    }

    [Fact]
    public void An_empty_role_list_carries_no_grant()
    {
        var token = Core().CreateChallengeToken(Guid.NewGuid(), "password", "acme", "github", []);

        MfaChallengeToken.ValidatedChallenge(Config, token)!.Grant.Should().BeNull();
    }

    [Fact]
    public void An_implementation_that_predates_the_member_still_loads_and_says_it_cannot_mint()
    {
        IMfaService older = new OlderImplementation();

        var act = () => older.CreateChallengeToken(Guid.NewGuid(), "password");

        act.Should().Throw<NotSupportedException>().WithMessage("*OlderImplementation*CreateChallengeToken*");
    }
}
