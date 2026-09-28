using System.Security.Claims;
using FluentAssertions;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// The capped overload on <see cref="ITokenIssuer"/> has a default implementation for issuers that
/// predate it. It cannot shorten a token it did not mint, so it must refuse rather than hand back one
/// that outlives the cap.
/// </summary>
public class TokenIssuerCapTests
{
    private sealed class UncappedIssuer(DateTime expiresAt) : ITokenIssuer
    {
        public Task<TokenIssueResult> IssueAccessTokenAsync(
            User user, string tenantSlug, IEnumerable<Claim>? extraClaims = null, CancellationToken ct = default) =>
            Task.FromResult(TokenIssueResult.Granted("jwt", "jti", expiresAt, Array.Empty<string>()));
    }

    private static readonly User AnyUser = new() { Id = Guid.NewGuid(), Username = "u" };

    [Fact]
    public async Task An_issuer_without_the_capped_overload_refuses_a_token_that_would_outlive_the_cap()
    {
        ITokenIssuer issuer = new UncappedIssuer(DateTime.UtcNow.AddMinutes(15));

        var result = await issuer.IssueAccessTokenAsync(AnyUser, "default", null, DateTime.UtcNow.AddMinutes(5));

        result.Allowed.Should().BeFalse();
    }

    [Fact]
    public async Task An_issuer_without_the_capped_overload_passes_through_a_token_inside_the_cap()
    {
        var expiresAt = DateTime.UtcNow.AddMinutes(15);
        ITokenIssuer issuer = new UncappedIssuer(expiresAt);

        var result = await issuer.IssueAccessTokenAsync(AnyUser, "default", null, DateTime.UtcNow.AddMinutes(30));

        result.Allowed.Should().BeTrue();
        result.ExpiresAt.Should().Be(expiresAt);
    }
}
