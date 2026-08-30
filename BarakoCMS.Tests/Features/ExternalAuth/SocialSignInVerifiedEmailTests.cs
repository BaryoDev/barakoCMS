using BarakoCMS.ExternalAuth;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace BarakoCMS.Tests.Features.ExternalAuth;

/// <summary>
/// A provider that has not verified the email does not get a token.
/// </summary>
/// <remarks>
/// The email was the only join key, so an unverified one was a login for whichever local account
/// held that address. A seeded SuperAdmin's is <c>{username}@company.com</c> and therefore
/// guessable, and <c>PasswordHash</c> is never consulted on this path.
///
/// This is the first test this module has ever had. It had no project reference from the test
/// project, so nothing could reach it, which is why three of four providers shipped without ever
/// asking whether the address they were handed had been verified. See #120.
/// </remarks>
[Collection("Sequential")]
public class SocialSignInVerifiedEmailTests
{
    private readonly IntegrationTestFixture _fixture;

    public SocialSignInVerifiedEmailTests(IntegrationTestFixture fixture) => _fixture = fixture;

    /// <summary>
    /// The takeover: an unverified assertion of an existing account's address mints nothing.
    /// </summary>
    /// <remarks>
    /// The victim is created first and the attacker asserts their exact address, which is the whole
    /// attack. Asserting only that the call was denied would also pass if the code refused
    /// everything, so the sibling test below signs the same address in with the flag set.
    /// </remarks>
    [Fact]
    public async Task An_unverified_email_does_not_mint_a_token_for_an_existing_account()
    {
        var victim = await CreateUserAsync("victim");

        var tokens = await IssueAsync(victim.Email, emailVerified: false);

        tokens.Allowed.Should().BeFalse(
            "an address the provider never verified must not be a login for the account holding it");
        tokens.Token.Should().BeEmpty();
        tokens.Refresh.Should().BeEmpty();
    }

    /// <summary>
    /// The positive control. Without it, refusing every sign-in would pass the test above.
    /// </summary>
    [Fact]
    public async Task A_verified_email_still_signs_in()
    {
        var user = await CreateUserAsync("returning");

        var tokens = await IssueAsync(user.Email, emailVerified: true);

        tokens.Allowed.Should().BeTrue("a verified address is the case this flow exists for");
        tokens.Token.Should().NotBeEmpty();
    }

    /// <summary>
    /// An unverified sign-in creates nothing either, so it cannot be used to squat an address.
    /// </summary>
    /// <remarks>
    /// The refusal happens before the lookup, which matters: refusing only when the account already
    /// exists would still let an attacker pre-create the account for an address they do not own and
    /// wait for the real owner to arrive.
    /// </remarks>
    [Fact]
    public async Task An_unverified_email_does_not_create_an_account()
    {
        var email = $"never-seen-{Guid.NewGuid():n}@example.com";

        var tokens = await IssueAsync(email, emailVerified: false);

        tokens.Allowed.Should().BeFalse();

        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var created = await session.Query<barakoCMS.Models.User>()
            .FirstOrDefaultAsync(u => u.Email == email);
        created.Should().BeNull("a refused sign-in must not leave an account behind");
    }

    private async Task<barakoCMS.Models.User> CreateUserAsync(string prefix)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var user = new barakoCMS.Models.User
        {
            Id = Guid.NewGuid(),
            Email = $"{prefix}-{Guid.NewGuid():n}@example.com",
            Username = $"{prefix}-{Guid.NewGuid():n}",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("a-real-password"),
        };
        session.Store(user);
        await session.SaveChangesAsync();
        return user;
    }

    private async Task<SocialSignIn.Tokens> IssueAsync(string email, bool emailVerified)
    {
        using var scope = _fixture.Services.CreateScope();
        var provider = scope.ServiceProvider;

        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Request.Headers["User-Agent"] = "social-sign-in-tests";

        return await SocialSignIn.IssueAsync(
            provider.GetRequiredService<IDocumentSession>(),
            provider.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>(),
            provider.GetRequiredService<barakoCMS.Core.Interfaces.IDeviceGate>(),
            provider.GetRequiredService<barakoCMS.Infrastructure.Auth.ITokenIssuer>(),
            provider.GetRequiredService<barakoCMS.Infrastructure.Auth.Mfa.IMfaService>(),
            context,
            email,
            emailVerified,
            barakoCMS.Models.Tenant.DefaultSlug,
            CancellationToken.None);
    }
}
