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
    private static IChangeSet OneInsertedEntry()
    {
        var commit = Substitute.For<IChangeSet>();
        commit.Inserted.Returns([new ContentDoc { Id = Guid.NewGuid(), ContentType = "post" }]);
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

        using (batch.Running())
        {
            await listener.AfterCommitAsync(session, OneInsertedEntry(), CancellationToken.None);
        }

        await store.DidNotReceiveWithAnyArgs().EvictByTagAsync(default!, default);

        await batch.RunAfterCommitAsync(CancellationToken.None);

        await store.ReceivedWithAnyArgs().EvictByTagAsync(default!, default);
    }

    [Fact]
    public async Task A_delivery_cache_eviction_outside_a_batch_runs_at_once()
    {
        var store = Substitute.For<IOutputCacheStore>();
        var listener = new DeliveryResponseCacheListener(store, NullLogger<DeliveryResponseCacheListener>.Instance);
        var session = Substitute.For<IDocumentSession>();

        await listener.AfterCommitAsync(session, OneInsertedEntry(), CancellationToken.None);

        await store.ReceivedWithAnyArgs().EvictByTagAsync(default!, default);
    }
}
