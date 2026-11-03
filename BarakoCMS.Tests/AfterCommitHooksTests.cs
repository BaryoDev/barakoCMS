using barakoCMS.Infrastructure.Caching;
using barakoCMS.Infrastructure.Multitenancy;
using FluentAssertions;
using JasperFx.Events;
using Marten;
using Marten.Services;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Weasel.Storage;
using Xunit;
using ContentDoc = barakoCMS.Models.Content;

namespace BarakoCMS.Tests;

/// <summary>
/// Work that follows a content batch's flush waits for the commit, and one failing item does not
/// stop the others.
/// </summary>
public class AfterCommitHooksTests
{
    private static IChangeSet OneInsertedEntry(Guid? id = null)
    {
        var commit = Substitute.For<IChangeSet>();
        commit.Inserted.Returns([new ContentDoc { Id = id ?? Guid.NewGuid(), ContentType = "post" }]);
        commit.Updated.Returns(Array.Empty<object>());
        commit.Deleted.Returns(Array.Empty<IDeletion>());
        commit.GetEvents().Returns(Array.Empty<IEvent>());
        return commit;
    }

    [Fact]
    public async Task A_held_item_that_throws_does_not_stop_the_items_after_it()
    {
        var batch = new BatchTransaction();
        var ran = new List<string>();
        using (batch.Running())
        {
            BatchTransaction.TryDeferUntilCommit(_ => throw new InvalidOperationException("first")).Should().BeTrue();
            BatchTransaction.TryDeferUntilCommit(_ => { ran.Add("second"); return Task.CompletedTask; }).Should().BeTrue();
        }

        var act = () => batch.RunAfterCommitAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
        ran.Should().Equal("second");
    }

    [Fact]
    public async Task A_delivery_cache_eviction_inside_a_batch_waits_for_the_commit()
    {
        var store = Substitute.For<IOutputCacheStore>();
        var listener = new DeliveryResponseCacheListener(store, NullLogger<DeliveryResponseCacheListener>.Instance);
        var session = Substitute.For<IDocumentSession>();
        var batch = new BatchTransaction();
        var id = Guid.NewGuid();
        var evicted = new List<string>();
        await store.EvictByTagAsync(Arg.Do<string>(evicted.Add), Arg.Any<CancellationToken>());

        using (batch.Running())
        {
            await listener.AfterCommitAsync(session, OneInsertedEntry(id), CancellationToken.None);
        }

        evicted.Should().BeEmpty();

        await batch.RunAfterCommitAsync(CancellationToken.None);

        var tenant = barakoCMS.Models.Tenant.DefaultSlug;
        evicted.Should().BeEquivalentTo(new[]
        {
            DeliveryCache.Tag(tenant, CacheScope.Entry(id)),
            DeliveryCache.Tag(tenant, CacheScope.Type("post")),
            DeliveryCache.Tag(tenant, CacheScope.Named("sitemap")),
            DeliveryCache.Tag(tenant, CacheScope.Named(DeliveryResponseCacheListener.UntypedReferences)),
        });
    }

    [Fact]
    public async Task Held_work_runs_with_a_token_that_a_cancelled_request_cannot_cancel()
    {
        var batch = new BatchTransaction();
        var seen = new List<bool>();
        using (batch.Running())
        {
            BatchTransaction.TryDeferUntilCommit(ct => { seen.Add(ct.IsCancellationRequested); return Task.CompletedTask; })
                .Should().BeTrue();
        }

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await batch.RunAfterCommitAsync(cancelled.Token);

        seen.Should().Equal(false);
    }

    [Fact]
    public async Task A_delivery_cache_eviction_outside_a_batch_runs_at_once()
    {
        var store = Substitute.For<IOutputCacheStore>();
        var listener = new DeliveryResponseCacheListener(store, NullLogger<DeliveryResponseCacheListener>.Instance);
        var session = Substitute.For<IDocumentSession>();
        var id = Guid.NewGuid();
        var evicted = new List<string>();
        await store.EvictByTagAsync(Arg.Do<string>(evicted.Add), Arg.Any<CancellationToken>());

        await listener.AfterCommitAsync(session, OneInsertedEntry(id), CancellationToken.None);

        var tenant = barakoCMS.Models.Tenant.DefaultSlug;
        var expected = new[]
        {
            DeliveryCache.Tag(tenant, CacheScope.Entry(id)),
            DeliveryCache.Tag(tenant, CacheScope.Type("post")),
            DeliveryCache.Tag(tenant, CacheScope.Named("sitemap")),
            DeliveryCache.Tag(tenant, CacheScope.Named(DeliveryResponseCacheListener.UntypedReferences)),
        };
        evicted.Should().NotBeEmpty();
        evicted.Should().BeEquivalentTo(expected);

        await new BatchTransaction().RunAfterCommitAsync(CancellationToken.None);

        evicted.Should().BeEquivalentTo(expected);
    }
}
