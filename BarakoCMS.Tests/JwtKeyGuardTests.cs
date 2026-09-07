using System;
using FluentAssertions;
using Xunit;
using barakoCMS.Infrastructure.Security;

namespace BarakoCMS.Tests;

/// <summary>
/// The JWT key guard fails fast on missing, short, or placeholder keys. The placeholder case is the
/// one a length check misses: k8s/02-secret.yaml ships a 45-character
/// "REPLACE_THIS_WITH_A_REAL_32_CHAR_SECRET_KEY", so an operator applying the manifests unedited
/// would boot on a key public in the repository and anyone could forge tokens.
/// </summary>
public class JwtKeyGuardTests
{
    [Fact]
    public void Rejects_the_shipped_k8s_placeholder_even_though_it_is_long_enough()
    {
        // the literal from k8s/02-secret.yaml, 45 chars, passes a length-only check
        var placeholder = "REPLACE_THIS_WITH_A_REAL_32_CHAR_SECRET_KEY";
        placeholder.Length.Should().BeGreaterThanOrEqualTo(JwtKeyGuard.MinLength);

        var act = () => JwtKeyGuard.Validate(placeholder);

        act.Should().Throw<InvalidOperationException>().WithMessage("*placeholder*");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("too-short")]
    public void Rejects_missing_or_short_keys(string? key)
    {
        var act = () => JwtKeyGuard.Validate(key);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Accepts_a_real_key_and_returns_it_unchanged()
    {
        var key = "this-is-a-real-random-looking-signing-key-0123456789";
        JwtKeyGuard.Validate(key).Should().Be(key);
    }
}
