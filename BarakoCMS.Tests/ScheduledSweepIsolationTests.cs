using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BarakoCMS.Tests;

[Collection("Integration1")]
public class ScheduledSweepIsolationTests
{
    private readonly IntegrationTestFixture _fixture;

    public ScheduledSweepIsolationTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private IDocumentStore Store() => _fixture.Services.GetRequiredService<IDocumentStore>();

    private async Task<(string Slug, Guid ContentId)> SeedDueTenantAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var slug = $"sweepiso-{Guid.NewGuid():N}"[..20];
        var contentId = Guid.NewGuid();

        await using var session = Store().LightweightSession(slug);
        session.Store(new Content
        {
            Id = contentId,
            ContentType = "article",
            Status = ContentStatus.Scheduled,
            ScheduledPublishAt = DateTime.UtcNow.AddMinutes(-5),
            Data = new Dictionary<string, object> { ["Title"] = "due" },
            CreatedAt = DateTime.UtcNow,
        });
        await session.SaveChangesAsync(ct);

        await using var admin = Store().LightweightSession();
        admin.Store(new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = slug });
        await admin.SaveChangesAsync(ct);

        return (slug, contentId);
    }

    private async Task<ContentStatus> StatusOfAsync(string slug, Guid id)
    {
        await using var session = Store().QuerySession(slug);
        var content = await session.LoadAsync<Content>(id, TestContext.Current.CancellationToken);
        content.Should().NotBeNull();
        return content!.Status;
    }

    [Fact]
    public async Task A_tenant_whose_sweep_throws_does_not_stop_the_tenants_after_it()
    {
        var first = await SeedDueTenantAsync();
        var second = await SeedDueTenantAsync();

        var calls = 0;
        var service = new ScheduledContentService(
            Store(), _fixture.Services.GetRequiredService<ILogger<ScheduledContentService>>())
        {
            AfterLoad = _ =>
            {
                if (Interlocked.Increment(ref calls) == 1) throw new InvalidOperationException("one tenant fails");
                return Task.CompletedTask;
            },
        };

        var swept = await service.TrySweepAllTenantsAsync(DateTime.UtcNow, TestContext.Current.CancellationToken);

        swept.Should().BeTrue("a failing tenant is logged and skipped, the sweep still ran");
        calls.Should().BeGreaterThan(1, "later tenants with due content were still swept after the first threw");

        var statuses = new[]
        {
            await StatusOfAsync(first.Slug, first.ContentId),
            await StatusOfAsync(second.Slug, second.ContentId),
        };
        statuses.Should().Contain(ContentStatus.Published, "a tenant after the failing one was still processed");
    }
}
