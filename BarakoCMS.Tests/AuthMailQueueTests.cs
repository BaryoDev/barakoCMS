using barakoCMS.Infrastructure.Auth;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BarakoCMS.Tests;

public class AuthMailQueueTests
{
    private static AuthMailQueue Queue() =>
        new(new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<AuthMailQueue>.Instance);

    [Fact]
    public void Two_messages_for_one_address_leave_one_queued_with_the_newer_body()
    {
        var queue = Queue();

        queue.Enqueue("Someone@Example.com", "Your code", "old");
        queue.Enqueue("someone@example.com", "Your code", "new");

        queue.PendingCount.Should().Be(1);
        queue.PendingBody("someone@example.com").Should().Be("new");
    }

    [Fact]
    public void A_full_queue_drops_the_message_and_counts_it()
    {
        var queue = Queue();
        for (var i = 0; i < AuthMailQueue.Capacity; i++)
            queue.Enqueue($"user{i}@example.com", "s", "b");
        queue.PendingCount.Should().Be(AuthMailQueue.Capacity);
        var before = AuthMailQueue.DroppedCounter.Value;

        queue.Enqueue("overflow@example.com", "s", "b");

        AuthMailQueue.DroppedCounter.Value.Should().Be(before + 1);
        queue.PendingCount.Should().Be(AuthMailQueue.Capacity);
        queue.PendingBody("overflow@example.com").Should().BeNull();
    }
}
