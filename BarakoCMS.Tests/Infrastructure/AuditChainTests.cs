using FluentAssertions;
using Xunit;
using barakoCMS.Infrastructure.Audit;

namespace BarakoCMS.Tests.Infrastructure;

public class AuditChainTests
{
    private static readonly DateTime Fixed = new(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Same_inputs_produce_the_same_hash()
    {
        var a = AuditChain.ComputeHash(AuditChain.GenesisHash, "default", "auth.login.succeeded", Guid.Empty, null, null, Fixed);
        var b = AuditChain.ComputeHash(AuditChain.GenesisHash, "default", "auth.login.succeeded", Guid.Empty, null, null, Fixed);

        a.Should().Be(b);
    }

    [Fact]
    public void Changing_any_field_changes_the_hash()
    {
        var baseline = AuditChain.ComputeHash(AuditChain.GenesisHash, "default", "auth.login.succeeded", Guid.Empty, null, null, Fixed);

        AuditChain.ComputeHash("different-prev", "default", "auth.login.succeeded", Guid.Empty, null, null, Fixed)
            .Should().NotBe(baseline, "the previous hash is part of the chain");
        AuditChain.ComputeHash(AuditChain.GenesisHash, "other-tenant", "auth.login.succeeded", Guid.Empty, null, null, Fixed)
            .Should().NotBe(baseline, "tenant is part of the chained content");
        AuditChain.ComputeHash(AuditChain.GenesisHash, "default", "auth.login.failed", Guid.Empty, null, null, Fixed)
            .Should().NotBe(baseline, "the action name is part of the chained content");
        AuditChain.ComputeHash(AuditChain.GenesisHash, "default", "auth.login.succeeded", Guid.NewGuid(), null, null, Fixed)
            .Should().NotBe(baseline, "the actor is part of the chained content");
        AuditChain.ComputeHash(AuditChain.GenesisHash, "default", "auth.login.succeeded", Guid.Empty, "Role", null, Fixed)
            .Should().NotBe(baseline, "the target type is part of the chained content");
        AuditChain.ComputeHash(AuditChain.GenesisHash, "default", "auth.login.succeeded", Guid.Empty, null, "abc", Fixed)
            .Should().NotBe(baseline, "the target id is part of the chained content");
        AuditChain.ComputeHash(AuditChain.GenesisHash, "default", "auth.login.succeeded", Guid.Empty, null, null, Fixed.AddSeconds(1))
            .Should().NotBe(baseline, "the timestamp is part of the chained content");
    }

    [Fact]
    public void Genesis_hash_is_a_valid_sha256_hex_shape()
    {
        AuditChain.GenesisHash.Should().HaveLength(64);
        AuditChain.GenesisHash.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void Real_hash_is_a_valid_sha256_hex_shape()
    {
        var hash = AuditChain.ComputeHash(AuditChain.GenesisHash, "default", "auth.login.succeeded", Guid.NewGuid(), null, null, Fixed);

        hash.Should().HaveLength(64);
        hash.Should().MatchRegex("^[0-9a-f]{64}$");
    }
}
