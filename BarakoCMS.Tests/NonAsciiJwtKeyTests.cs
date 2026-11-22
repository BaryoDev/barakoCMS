using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Auth.Mfa;
using barakoCMS.Infrastructure.Preview;
using barakoCMS.Infrastructure.Security;
using barakoCMS.Infrastructure.Viewers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// A <c>JWT:Key</c> with a character outside ASCII (#1411). Minting reads the key as ASCII, so the
/// MFA challenge, step-up, preview and viewer validators must too, or every one of those tokens
/// fails on such a site while access tokens work.
/// </summary>
public class NonAsciiJwtKeyTests
{
    private static readonly IConfiguration Config = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["JWT:Key"] = "clé-secrète-with-accents-that-is-long-enough-for-the-guard",
            ["JWT:Issuer"] = "tests",
            ["JWT:Audience"] = "tests",
        })
        .Build();

    private static JwtMinter Minter() => new(Config, TimeProvider.System);

    [Fact]
    public void The_key_is_long_enough_for_the_guard()
    {
        var act = () => JwtKeyGuard.Validate(Config["JWT:Key"]);
        act.Should().NotThrow();
    }

    [Fact]
    public void An_MFA_challenge_validates_with_a_non_ASCII_key()
    {
        var userId = Guid.NewGuid();

        var (token, _) = MfaChallengeToken.Create(Minter(), Config, userId);

        MfaChallengeToken.ValidatedChallenge(Config, token)!.UserId.Should().Be(userId);
    }

    [Fact]
    public void A_step_up_token_validates_with_a_non_ASCII_key()
    {
        var userId = Guid.NewGuid();

        var (token, _) = StepUpToken.Create(Minter(), Config, userId);

        StepUpToken.ValidatedUserId(Config, token).Should().Be(userId);
    }

    [Fact]
    public void A_preview_token_validates_with_a_non_ASCII_key()
    {
        var entryId = Guid.NewGuid();

        var (token, _) = PreviewToken.Create(Minter(), Config, "acme", "article", "wip", entryId);

        PreviewToken.ValidatedEntryId(Config, token, "acme", "article", "wip").Should().Be(entryId);
    }

    [Fact]
    public void A_viewer_token_validates_with_a_non_ASCII_key()
    {
        var (token, _) = ViewerToken.Create(Minter(), Config, "acme", "member-1");

        ViewerToken.Validate(Config, token, "acme")!.Subject.Should().Be("member-1");
    }

    [Fact]
    public void The_guard_warns_about_a_non_ASCII_key_and_does_not_throw()
    {
        var key = Config["JWT:Key"]!;

        JwtKeyGuard.Validate(key).Should().Be(key);
        JwtKeyGuard.NonAsciiWarning(key).Should().Contain("ASCII");
    }

    [Fact]
    public void The_guard_stays_quiet_about_an_ASCII_key()
    {
        JwtKeyGuard.NonAsciiWarning("this-is-a-real-random-looking-signing-key-0123456789").Should().BeNull();
    }
}
