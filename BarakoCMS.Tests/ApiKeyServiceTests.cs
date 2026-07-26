using Xunit;
using FluentAssertions;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;

namespace BarakoCMS.Tests;

/// <summary>Unit cover for key generation, hashing, and scope matching (no DB).</summary>
public class ApiKeyServiceTests
{
    private readonly ApiKeyService _svc = new();

    [Fact]
    public void Generate_ProducesPrefixedSecret_AndAMatchingHash()
    {
        var k = _svc.Generate();

        k.Secret.Should().StartWith("bcms_");
        k.DisplayPrefix.Should().StartWith("bcms_");
        k.Secret.Should().StartWith(k.DisplayPrefix, "the display prefix is the front of the secret");
        k.DisplayPrefix.Length.Should().BeLessThan(k.Secret.Length, "the prefix must never be the whole secret");
        ApiKeyService.Hash(k.Secret).Should().Be(k.Hash, "the stored hash must match the secret");
    }

    [Fact]
    public void Generate_IsUnique_AndHashIsNotReversible()
    {
        var a = _svc.Generate();
        var b = _svc.Generate();

        a.Secret.Should().NotBe(b.Secret);
        a.Hash.Should().NotBe(b.Hash);
        a.Hash.Should().NotContain(a.Secret, "the hash must not embed the secret");
    }

    [Fact]
    public void Hash_IsDeterministic()
    {
        ApiKeyService.Hash("bcms_abc").Should().Be(ApiKeyService.Hash("bcms_abc"));
        ApiKeyService.Hash("bcms_abc").Should().NotBe(ApiKeyService.Hash("bcms_abd"));
    }

    [Theory]
    [InlineData("bcms_x", true)]
    [InlineData("eyJhbGc.jwt.token", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void LooksLikeApiKey_OnlyMatchesOurPrefix(string? token, bool expected)
    {
        ApiKeyService.LooksLikeApiKey(token).Should().Be(expected);
    }

    [Theory]
    [InlineData(new[] { "content:read" }, "content:read", true)]
    [InlineData(new[] { "content:read" }, "content:write", false)]
    [InlineData(new[] { "*" }, "content:write", true)]      // wildcard satisfies anything
    [InlineData(new[] { "content:read", "content:write" }, "content:write", true)]
    [InlineData(new string[0], "content:read", false)]      // no scopes satisfies nothing
    public void Satisfies_EnforcesScopes(string[] granted, string required, bool expected)
    {
        ApiKeyScopes.Satisfies(granted, required).Should().Be(expected);
    }

    [Theory]
    [InlineData("content:read", true)]
    [InlineData("*", true)]
    [InlineData("admin", false)]        // platform admin is deliberately not a key scope
    [InlineData("users:delete", false)]
    public void IsKnown_RejectsUnknownScopes(string scope, bool expected)
    {
        ApiKeyScopes.IsKnown(scope).Should().Be(expected);
    }
}
