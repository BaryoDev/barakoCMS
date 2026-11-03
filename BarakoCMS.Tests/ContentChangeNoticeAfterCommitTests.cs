using barakoCMS.Core.Interfaces;
using barakoCMS.Events;
using barakoCMS.Features.Public.Events;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using BarakoCMS.Tests.Builders;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// A live notice for a content batch is sent once the batch has committed, not when a write in it
/// is flushed, and a batch that rolls back announces nothing.
/// </summary>
[Collection("Integration1")]
public class ContentChangeNoticeAfterCommitTests
{
    private readonly IntegrationTestFixture _factory;

    public ContentChangeNoticeAfterCommitTests(IntegrationTestFixture factory) => _factory = factory;

    private static string TypeName() => "notice_" + Guid.NewGuid().ToString("n")[..8];

    private async Task<(ContentChangeBroadcaster.Subscription Subscription, IContentBatchRunner Runner, IServiceScope Scope)>
        ArrangeAsync(string type)
    {
        var host = _factory.WithSettings(new Dictionary<string, string?> { ["Delivery:Events:Enabled"] = "true" });
        var scope = host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<barakoCMS.Infrastructure.Multitenancy.TenantContext>().Slug =
            barakoCMS.Models.Tenant.DefaultSlug;

        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new ContentTypeBuilder().Named(type).PubliclyDeliverable().WithTitleAndSlug().Build());
        await session.SaveChangesAsync();

        var subscription = host.Services.GetRequiredService<ContentChangeBroadcaster>().TrySubscribe(
            barakoCMS.Models.Tenant.DefaultSlug, [type], "notice-test", 1000, 0, out _);
        subscription.Should().NotBeNull();

        return (subscription!, scope.ServiceProvider.GetRequiredService<IContentBatchRunner>(), scope);
    }

    private static async Task<Content> PublishAsync(IServiceProvider sp, string type, CancellationToken ct)
    {
        var slug = "n-" + Guid.NewGuid().ToString("n")[..8];
        var created = new ContentCreated(
            Guid.NewGuid(), type,
            new Dictionary<string, object> { ["Title"] = slug, ["Slug"] = slug },
            ContentStatus.Published, Guid.NewGuid(), slug, SensitivityLevel.Public, DateTime.UtcNow);
        var content = await sp.GetRequiredService<IContentWriter>().CreateAsync(created, ct);
        await sp.GetRequiredService<IDocumentSession>().SaveChangesAsync(ct);
        return content;
    }

    [Fact]
    public async Task A_notice_for_a_batch_is_sent_after_the_commit_and_not_at_the_flush()
    {
        var type = TypeName();
        var (subscription, runner, scope) = await ArrangeAsync(type);
        using var heldScope = scope;
        using var heldSubscription = subscription;
        var sentBeforeCommit = true;

        var content = await runner.RunAsync(async (sp, ct) =>
        {
            var created = await PublishAsync(sp, type, ct);
            sentBeforeCommit = subscription.Reader.TryPeek(out _);
            return new ContentBatchOutcome<Content>(created, true);
        }, CancellationToken.None);

        sentBeforeCommit.Should().BeFalse();
        subscription.Reader.TryRead(out var change).Should().BeTrue();
        change!.Id.Should().Be(content.Id);
    }

    [Fact]
    public async Task A_batch_that_rolls_back_sends_no_notice()
    {
        var type = TypeName();
        var (subscription, runner, scope) = await ArrangeAsync(type);
        using var heldScope = scope;
        using var heldSubscription = subscription;

        await runner.RunAsync(async (sp, ct) =>
            new ContentBatchOutcome<Content>(await PublishAsync(sp, type, ct), false), CancellationToken.None);

        subscription.Reader.TryPeek(out _).Should().BeFalse();
    }
}
