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

    [Fact]
    public void Rejects_the_compose_development_key_outside_Development_and_allows_it_in_Development()
    {
        const string composeKey = "dev-only-key-minimum-32-chars-long";

        var production = () => JwtKeyGuard.Validate(composeKey, isDevelopment: false);
        production.Should().Throw<InvalidOperationException>().WithMessage("*development key*");

        JwtKeyGuard.Validate(composeKey, isDevelopment: true).Should().Be(composeKey);
    }

    /// <summary>Every JWT key the repository shows as an example, and where it is shown.</summary>
    public static TheoryData<string> ExampleKeys() => new()
    {
        "dev-only-key-minimum-32-chars-long",                  // docker-compose.yml and docker-compose.hub.yml
        "change-me-to-at-least-32-characters-secret",          // quickstart/.env.example
        "local-only-signing-key-at-least-32-characters",       // docs/your-first-module.md
    };

    [Theory]
    [MemberData(nameof(ExampleKeys))]
    public void Rejects_every_example_key_outside_Development_and_allows_it_in_Development(string example)
    {
        var production = () => JwtKeyGuard.Validate(example, isDevelopment: false);
        var message = production.Should().Throw<InvalidOperationException>().WithMessage("*JWT:Key*").Which.Message;
        message.Should().NotContain(example, "the message names the setting and the rule, never the value");

        JwtKeyGuard.Validate(example, isDevelopment: true).Should().Be(example);
    }

    [Theory]
    [MemberData(nameof(ExampleKeys))]
    public void Reads_an_example_key_trimmed_and_without_regard_to_case(string example)
    {
        var act = () => JwtKeyGuard.Validate($"  {example.ToUpperInvariant()}\t", isDevelopment: false);

        act.Should().Throw<InvalidOperationException>().WithMessage("*example*");
    }

    [Fact]
    public void Rejects_the_key_in_the_quickstart_env_example()
    {
        // The file people copy to .env and are told to edit. If it passes the guard, a copy that
        // was never edited boots on a key printed in this repository.
        var line = File.ReadAllLines(Path.Combine(RepoPaths.Root, "quickstart", ".env.example"))
            .Single(l => l.StartsWith("JWT_KEY=", StringComparison.Ordinal));
        var key = line["JWT_KEY=".Length..];
        key.Length.Should().BeGreaterThanOrEqualTo(JwtKeyGuard.MinLength, "so only the example rule can refuse it");

        var act = () => JwtKeyGuard.Validate(key, isDevelopment: false);

        act.Should().Throw<InvalidOperationException>();
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
