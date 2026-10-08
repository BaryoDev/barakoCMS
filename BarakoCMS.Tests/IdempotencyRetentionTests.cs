using barakoCMS.Infrastructure.Filters;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BarakoCMS.Tests;

[Collection("Sequential")]
public class IdempotencyRetentionTests
{
    private readonly IntegrationTestFixture _factory;

    public IdempotencyRetentionTests(IntegrationTestFixture factory) => _factory = factory;

    private async Task SeedAsync(params IdempotencyRecord[] records)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.StoreObjects(records);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<List<string>> SurvivingAsync(string prefix)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return (await session.Query<IdempotencyRecord>()
            .Where(r => r.Key.StartsWith(prefix))
            .Select(r => r.Key)
            .ToListAsync(TestContext.Current.CancellationToken)).ToList();
    }

    [Fact]
    public async Task The_sweep_deletes_expired_keys_and_keeps_live_ones()
    {
        var prefix = $"sweep-{Guid.NewGuid():N}-";
        var now = DateTime.UtcNow;
        await SeedAsync(
            new IdempotencyRecord { Key = prefix + "old", Completed = true, CreatedAt = now.AddHours(-25) },
            new IdempotencyRecord { Key = prefix + "older", Completed = true, CreatedAt = now.AddDays(-10) },
            new IdempotencyRecord { Key = prefix + "live", Completed = true, CreatedAt = now.AddHours(-23) },
            new IdempotencyRecord { Key = prefix + "fresh", Completed = false, CreatedAt = now });

        (await SurvivingAsync(prefix)).Should().HaveCount(4);

        await using (var session = _factory.Services.GetRequiredService<IDocumentStore>().LightweightSession())
        {
            var removed = await IdempotencyRetentionService.SweepAsync(
                session, now, TimeSpan.FromHours(24), TestContext.Current.CancellationToken);
            removed.Should().BeGreaterThanOrEqualTo(2);
        }

        var surviving = await SurvivingAsync(prefix);
        surviving.Should().HaveCount(2);
        surviving.Should().BeEquivalentTo([prefix + "live", prefix + "fresh"]);
    }

    [Fact]
    public async Task A_full_tick_uses_the_configured_window()
    {
        var prefix = $"tick-{Guid.NewGuid():N}-";
        var now = DateTime.UtcNow;
        await SeedAsync(
            new IdempotencyRecord { Key = prefix + "two-hours", Completed = true, CreatedAt = now.AddHours(-2) },
            new IdempotencyRecord { Key = prefix + "now", Completed = true, CreatedAt = now });

        var service = new IdempotencyRetentionService(
            _factory.Services.GetRequiredService<IDocumentStore>(),
            new IdempotencyOptions { KeyHours = 1 },
            NullLogger<IdempotencyRetentionService>.Instance);

        var removed = await service.TrySweepAsync(now, TestContext.Current.CancellationToken);
        removed.Should().NotBeNull("no other instance holds the sweep lock in this test");

        var surviving = await SurvivingAsync(prefix);
        surviving.Should().HaveCount(1);
        surviving.Should().BeEquivalentTo([prefix + "now"]);
    }
}
