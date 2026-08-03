using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Infrastructure.Audit;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Infrastructure;

/// <summary>
/// <see cref="AuditChainTests"/> only exercises the pure hash math. These tests exercise
/// <see cref="AuditLog.RecordAsync"/> against a real Marten session, so they cover the "look up the
/// previous entry" half of the chain that the pure tests can't reach.
/// </summary>
[Collection("Sequential")]
public class AuditLogIntegrationTests
{
    private readonly IntegrationTestFixture _factory;

    public AuditLogIntegrationTests(IntegrationTestFixture factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task First_entry_in_a_tenant_chains_off_the_genesis_hash()
    {
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        var tenant = $"tenant_{Guid.NewGuid():N}";

        using var session = store.LightweightSession();
        await AuditLog.RecordAsync(session, tenant, "auth.login.succeeded", Guid.NewGuid(), "alice");
        await session.SaveChangesAsync();

        using var read = store.QuerySession();
        var entry = await read.Query<AuditEvent>().Where(e => e.TenantSlug == tenant).SingleAsync();

        entry.PrevHash.Should().Be(AuditChain.GenesisHash);
        entry.Hash.Should().NotBe(AuditChain.GenesisHash);
    }

    [Fact]
    public async Task Second_entry_chains_off_the_first_entrys_hash()
    {
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        var tenant = $"tenant_{Guid.NewGuid():N}";

        using (var session = store.LightweightSession())
        {
            await AuditLog.RecordAsync(session, tenant, "auth.login.succeeded", Guid.NewGuid(), "alice");
            await session.SaveChangesAsync();
        }

        using (var session = store.LightweightSession())
        {
            await AuditLog.RecordAsync(session, tenant, "role.deleted", Guid.NewGuid(), "alice", "Role", Guid.NewGuid().ToString());
            await session.SaveChangesAsync();
        }

        using var read = store.QuerySession();
        var entries = await read.Query<AuditEvent>()
            .Where(e => e.TenantSlug == tenant)
            .OrderBy(e => e.CreatedAt)
            .ToListAsync();

        entries.Should().HaveCount(2);
        entries[1].PrevHash.Should().Be(entries[0].Hash, "the second entry must chain off the first");
    }

    [Fact]
    public async Task Chains_for_different_tenants_do_not_interfere()
    {
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        var tenantA = $"tenant_{Guid.NewGuid():N}";
        var tenantB = $"tenant_{Guid.NewGuid():N}";

        using (var session = store.LightweightSession())
        {
            await AuditLog.RecordAsync(session, tenantA, "auth.login.succeeded", Guid.NewGuid(), "alice");
            await session.SaveChangesAsync();
        }

        using (var session = store.LightweightSession())
        {
            await AuditLog.RecordAsync(session, tenantB, "auth.login.succeeded", Guid.NewGuid(), "bob");
            await session.SaveChangesAsync();
        }

        using var read = store.QuerySession();
        var entryB = await read.Query<AuditEvent>().Where(e => e.TenantSlug == tenantB).SingleAsync();

        entryB.PrevHash.Should().Be(AuditChain.GenesisHash,
            "tenant B's chain must start fresh even though tenant A already has an entry");
    }

    [Fact]
    public async Task Recorded_fields_round_trip_through_marten()
    {
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        var tenant = $"tenant_{Guid.NewGuid():N}";
        var actorId = Guid.NewGuid();
        var targetId = Guid.NewGuid().ToString();

        using (var session = store.LightweightSession())
        {
            await AuditLog.RecordAsync(
                session, tenant, "role.deleted", actorId, "alice",
                targetType: "Role", targetId: targetId,
                metadata: new Dictionary<string, object> { ["name"] = "Editor" },
                ipAddress: "203.0.113.7");
            await session.SaveChangesAsync();
        }

        using var read = store.QuerySession();
        var entry = await read.Query<AuditEvent>().Where(e => e.TenantSlug == tenant).SingleAsync();

        entry.Action.Should().Be("role.deleted");
        entry.ActorUserId.Should().Be(actorId);
        entry.ActorUsername.Should().Be("alice");
        entry.TargetType.Should().Be("Role");
        entry.TargetId.Should().Be(targetId);
        entry.IpAddress.Should().Be("203.0.113.7");
    }
}
