using FluentAssertions;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using barakoCMS.Infrastructure.Retention;
using barakoCMS.Models;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// The retention primitive on its own, over webhook deliveries. Each test scopes its declaration to
/// a marker URL and its tenant list to its own slugs, so a pass cannot touch rows other tests own.
/// </summary>
[Collection("Integration1")]
public class RetentionPrimitiveTests(IntegrationTestFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

    private IDocumentStore Store => fixture.Services.GetRequiredService<IDocumentStore>();
    private IConfiguration Config => fixture.Services.GetRequiredService<IConfiguration>();

    private static string PartitionsSql(params string[] tenants) =>
        "select unnest(array[" + string.Join(",", tenants.Select(t => $"'{t}'")) + "])";

    private static RetentionDeclaration Declaration(string marker, Func<string, TimeSpan?> windowFor) =>
        new RetentionDeclaration<WebhookDelivery>(
            "test deliveries",
            cutoff => d => d.Url == marker && d.CreatedAt < cutoff,
            windowFor,
            d => d.Url == marker);

    private async Task<Guid> SeedAsync(string tenant, string marker, int ageDays)
    {
        var delivery = new WebhookDelivery
        {
            Id = Guid.NewGuid(),
            WorkflowId = Guid.NewGuid(),
            Url = marker,
            Event = "Published",
            CreatedAt = Now.AddDays(-ageDays),
        };

        await using var session = Store.LightweightSession(tenant);
        session.Store(delivery);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return delivery.Id;
    }

    private async Task<bool> ExistsAsync(string tenant, Guid id)
    {
        await using var session = Store.QuerySession(tenant);
        return await session.LoadAsync<WebhookDelivery>(id, TestContext.Current.CancellationToken) is not null;
    }

    private static string NewMarker() => "https://retention.test/" + Guid.NewGuid().ToString("N");

    private static long NewLockKey() => 9_000_000_000L + Random.Shared.Next(1, int.MaxValue);

    private Task<RetentionSweepResult> SweepAsync(
        long lockKey, string[] tenants, RetentionDeclaration declaration) =>
        RetentionSweeper.TrySweepAllTenantsAsync(
            Store, Config, lockKey, PartitionsSql(tenants), [declaration], Now,
            NullLogger.Instance, "Test", TestContext.Current.CancellationToken);

    [Fact]
    public async Task A_pass_skips_when_another_node_holds_the_lock_and_deletes_once_it_is_free()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenant = $"ret-{Guid.NewGuid():N}"[..20];
        var marker = NewMarker();
        var key = NewLockKey();
        var old = await SeedAsync(tenant, marker, ageDays: 40);
        (await ExistsAsync(tenant, old)).Should().BeTrue("the seed has to exist before the sweep means anything");

        var declaration = Declaration(marker, _ => TimeSpan.FromDays(7));

        await using (var other = Store.Storage.Database.CreateConnection())
        {
            await other.OpenAsync(ct);
            await using (var hold = other.CreateCommand())
            {
                hold.CommandText = "select pg_advisory_lock(@key)";
                hold.Parameters.AddWithValue("key", key);
                await hold.ExecuteScalarAsync(ct);
            }

            var skipped = await SweepAsync(key, [tenant], declaration);

            skipped.LockHeld.Should().BeFalse();
            skipped.Removed.Should().Be(0);
            (await ExistsAsync(tenant, old)).Should().BeTrue("a node that did not get the lock deletes nothing");
        }

        var ran = await SweepAsync(key, [tenant], declaration);

        ran.LockHeld.Should().BeTrue();
        ran.Removed.Should().Be(1);
        (await ExistsAsync(tenant, old)).Should().BeFalse("with the lock free the same pass removes the row");
    }

    [Fact]
    public async Task A_tenant_with_a_longer_window_keeps_rows_another_tenant_loses()
    {
        var shortTenant = $"ret-s-{Guid.NewGuid():N}"[..20];
        var longTenant = $"ret-l-{Guid.NewGuid():N}"[..20];
        var marker = NewMarker();
        var inShort = await SeedAsync(shortTenant, marker, ageDays: 40);
        var inLong = await SeedAsync(longTenant, marker, ageDays: 40);
        (await ExistsAsync(shortTenant, inShort)).Should().BeTrue();
        (await ExistsAsync(longTenant, inLong)).Should().BeTrue();

        var declaration = Declaration(
            marker, tenant => tenant == longTenant ? TimeSpan.FromDays(90) : TimeSpan.FromDays(7));

        var result = await SweepAsync(NewLockKey(), [shortTenant, longTenant], declaration);

        result.Removed.Should().Be(1);
        (await ExistsAsync(shortTenant, inShort)).Should().BeFalse("40 days is past its 7 day window");
        (await ExistsAsync(longTenant, inLong)).Should().BeTrue("40 days is inside its 90 day window");
    }

    [Fact]
    public async Task A_tenant_holding_no_rows_is_skipped_and_counted()
    {
        var full = $"ret-f-{Guid.NewGuid():N}"[..20];
        var empty = $"ret-e-{Guid.NewGuid():N}"[..20];
        var marker = NewMarker();
        var seeded = await SeedAsync(full, marker, ageDays: 40);
        (await ExistsAsync(full, seeded)).Should().BeTrue("the swept tenant must hold a row for the skip to mean anything");

        var result = await SweepAsync(
            NewLockKey(), [full, empty], Declaration(marker, _ => TimeSpan.FromDays(7)));

        result.TenantsSwept.Should().Be(1);
        result.TenantsSkipped.Should().Be(1);
        result.Removed.Should().Be(1);
    }

    [Fact]
    public async Task A_zero_window_keeps_everything()
    {
        var tenant = $"ret-{Guid.NewGuid():N}"[..20];
        var marker = NewMarker();
        var old = await SeedAsync(tenant, marker, ageDays: 4000);
        (await ExistsAsync(tenant, old)).Should().BeTrue();

        var result = await SweepAsync(NewLockKey(), [tenant], Declaration(marker, _ => TimeSpan.Zero));

        result.Removed.Should().Be(0);
        (await ExistsAsync(tenant, old)).Should().BeTrue("zero or less means keep forever");
    }
}
