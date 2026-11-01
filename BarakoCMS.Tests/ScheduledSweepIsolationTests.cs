using barakoCMS.Features.Workflows;
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
        var ct = TestContext.Current.CancellationToken;
        var logger = _fixture.Services.GetRequiredService<ILogger<ScheduledContentService>>();

        // Leftover due content in other tenants would take the one throw, so clear it first.
        (await new ScheduledContentService(Store(), logger).TrySweepAllTenantsAsync(DateTime.UtcNow, ct))
            .Should().BeTrue();

        var first = await SeedDueTenantAsync();
        var second = await SeedDueTenantAsync();

        var calls = 0;
        var service = new ScheduledContentService(Store(), logger)
        {
            AfterLoad = _ =>
            {
                if (Interlocked.Increment(ref calls) == 1) throw new InvalidOperationException("one tenant fails");
                return Task.CompletedTask;
            },
        };

        var swept = false;
        var act = async () => swept = await service.TrySweepAllTenantsAsync(DateTime.UtcNow, ct);
        await act.Should().NotThrowAsync();

        swept.Should().BeTrue("a failing tenant is logged and skipped, the sweep still ran");
        calls.Should().Be(2, "both tenants had due content, so the hook ran for the failing one and the one after it");

        var statuses = new[]
        {
            await StatusOfAsync(first.Slug, first.ContentId),
            await StatusOfAsync(second.Slug, second.ContentId),
        };
        statuses.Should().HaveCount(2);
        statuses.Should().BeEquivalentTo(new[] { ContentStatus.Scheduled, ContentStatus.Published },
            "the tenant whose sweep threw is left as it was and the other was still published");
    }

    [Fact]
    public async Task A_tenant_whose_content_sweep_throws_still_fires_its_due_schedule_workflow()
    {
        var ct = TestContext.Current.CancellationToken;
        var logger = _fixture.Services.GetRequiredService<ILogger<ScheduledContentService>>();

        (await new ScheduledContentService(Store(), logger).TrySweepAllTenantsAsync(DateTime.UtcNow, ct))
            .Should().BeTrue();

        var tenant = await SeedDueTenantAsync();
        var workflowId = Guid.NewGuid();
        await using (var session = Store().LightweightSession(tenant.Slug))
        {
            session.Store(new WorkflowDefinition
            {
                Id = workflowId,
                Name = "sweepiso-schedule",
                TriggerContentType = "article",
                TriggerEvent = WorkflowEvents.Schedule,
                TriggerEvents = [WorkflowEvents.Schedule],
                Schedule = "* * * * *",
                ScheduleTimeZone = "UTC",
                ScheduleArmedAt = DateTime.UtcNow.AddHours(-2),
                Actions = [new WorkflowAction { Type = "Email", Parameters = new() { ["To"] = "a@b.com", ["Subject"] = "s", ["Body"] = "b" } }],
            });
            await session.SaveChangesAsync(ct);
        }

        var service = new ScheduledContentService(Store(), logger)
        {
            AfterLoad = _ => throw new InvalidOperationException("the content sweep fails"),
        };
        (await service.TrySweepAllTenantsAsync(DateTime.UtcNow, ct)).Should().BeTrue();

        await using var read = Store().QuerySession(tenant.Slug);
        var claims = await read.Query<ScheduleClaim>().Where(c => c.WorkflowId == workflowId).ToListAsync(ct);
        claims.Should().HaveCount(1, "a failing content sweep must not skip the schedule workflows of the same tenant");
        (await StatusOfAsync(tenant.Slug, tenant.ContentId)).Should().Be(ContentStatus.Scheduled);
    }
}
