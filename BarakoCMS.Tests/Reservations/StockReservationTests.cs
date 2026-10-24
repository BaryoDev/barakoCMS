using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Reservations;
using BarakoCMS.Tests.SampleProbe;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BarakoCMS.Tests.Reservations;

[Collection("Integration1")]
public class StockReservationTests(SampleProbeHost host) : IClassFixture<SampleProbeHost>
{
    private IStockReservations Stock => host.Services.GetRequiredService<IStockReservations>();

    private static string Fresh() => $"stock-{Guid.NewGuid():N}"[..24];

    private async Task ReceiveAsync(string counter, long quantity, long floor = 0, string? tenant = null)
    {
        await using var session = host.OpenSession(tenant);
        await Stock.ReceiveAsync(session, counter, quantity, "delivery", floor);
        await session.SaveChangesAsync();
    }

    private async Task<StockReservation> ReserveAsync(string counter, long quantity, TimeSpan? holdFor = null, string? tenant = null)
    {
        await using var session = host.OpenSession(tenant);
        var result = await Stock.ReserveAsync(session, counter, quantity, holdFor, "order");
        await session.SaveChangesAsync();
        return result;
    }

    private async Task<long?> BalanceAsync(string counter, string? tenant = null)
    {
        await using var session = host.OpenSession(tenant);
        return await Stock.BalanceAsync(session, counter);
    }

    [Fact]
    public async Task Two_racing_reservations_for_the_last_unit_accept_one_and_refuse_the_other()
    {
        var counter = Fresh();
        await ReceiveAsync(counter, 1);

        var results = await Task.WhenAll(ReserveAsync(counter, 1), ReserveAsync(counter, 1));

        results.Should().HaveCount(2);
        results.Count(r => r.Succeeded).Should().Be(1);
        results.Count(r => !r.Succeeded).Should().Be(1);
        (await BalanceAsync(counter)).Should().Be(0);
    }

    [Fact]
    public async Task Thirty_racing_reservations_for_five_units_accept_exactly_five()
    {
        var counter = Fresh();
        await ReceiveAsync(counter, 5);

        var results = await Task.WhenAll(Enumerable.Range(0, 30).Select(_ => ReserveAsync(counter, 1)));

        results.Should().HaveCount(30);
        results.Count(r => r.Succeeded).Should().Be(5);
        results.Where(r => r.Succeeded).Select(r => r.HoldId).Should().OnlyHaveUniqueItems();
        (await BalanceAsync(counter)).Should().Be(0);
    }

    [Fact]
    public async Task A_reservation_never_leaves_the_balance_under_the_floor()
    {
        var counter = Fresh();
        await ReceiveAsync(counter, 10, floor: 4);

        var tooMany = await ReserveAsync(counter, 7);
        var fits = await ReserveAsync(counter, 6);
        var nothingLeft = await ReserveAsync(counter, 1);

        tooMany.Succeeded.Should().BeFalse();
        tooMany.Balance.Should().Be(10);
        fits.Succeeded.Should().BeTrue();
        fits.Balance.Should().Be(4);
        nothingLeft.Succeeded.Should().BeFalse();
        (await BalanceAsync(counter)).Should().Be(4);
    }

    [Fact]
    public async Task Reserving_against_a_counter_that_does_not_exist_is_refused()
    {
        var result = await ReserveAsync(Fresh(), 1);

        result.Succeeded.Should().BeFalse();
        result.HoldId.Should().BeNull();
        result.Balance.Should().Be(0);
    }

    [Fact]
    public async Task A_reservation_and_a_content_save_commit_together_so_the_loser_saves_nothing()
    {
        var counter = Fresh();
        await ReceiveAsync(counter, 1);
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };

        async Task<bool> RegisterAsync(Guid id)
        {
            await using var session = host.OpenSession();
            var reservation = await Stock.ReserveAsync(session, counter, 1);
            if (reservation.Succeeded)
                session.Events.StartStream(id, new ProbeTicketIssued(id, counter));
            await session.SaveChangesAsync();
            return reservation.Succeeded;
        }

        var outcomes = await Task.WhenAll(ids.Select(RegisterAsync));

        outcomes.Count(o => o).Should().Be(1);
        await using var read = host.OpenSession();
        var states = await Task.WhenAll(ids.Select(id => read.Events.FetchStreamStateAsync(id)));
        states.Count(s => s is not null).Should().Be(1);
    }

    [Fact]
    public async Task A_reservation_in_a_session_that_is_never_saved_gives_its_unit_back()
    {
        var counter = Fresh();
        await ReceiveAsync(counter, 1);

        await using (var abandoned = host.OpenSession())
        {
            (await Stock.ReserveAsync(abandoned, counter, 1)).Succeeded.Should().BeTrue();
        }

        (await BalanceAsync(counter)).Should().Be(1);
        (await ReserveAsync(counter, 1)).Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task A_content_save_that_fails_takes_the_reservation_with_it()
    {
        var counter = Fresh();
        await ReceiveAsync(counter, 1);
        var id = Guid.NewGuid();

        await using (var first = host.OpenSession())
        {
            first.Events.StartStream(id, new ProbeTicketIssued(id, "first"));
            await first.SaveChangesAsync();
        }

        await using (var failing = host.OpenSession())
        {
            (await Stock.ReserveAsync(failing, counter, 1)).Succeeded.Should().BeTrue();
            failing.Events.StartStream(id, new ProbeTicketIssued(id, "duplicate"));
            var save = async () => await failing.SaveChangesAsync();
            await save.Should().ThrowAsync<Exception>();
        }

        (await BalanceAsync(counter)).Should().Be(1);
    }

    [Fact]
    public async Task A_hold_that_is_not_confirmed_returns_its_unit_when_it_expires()
    {
        var counter = Fresh();
        await ReceiveAsync(counter, 1);
        var hold = await ReserveAsync(counter, 1, TimeSpan.FromMinutes(10));
        hold.Succeeded.Should().BeTrue();
        (await ReserveAsync(counter, 1)).Succeeded.Should().BeFalse();

        await using (var sweep = host.OpenSession())
        {
            (await Stock.ReleaseExpiredAsync(sweep, DateTimeOffset.UtcNow.AddMinutes(11))).Should().BeGreaterThanOrEqualTo(1);
            await sweep.SaveChangesAsync();
        }

        (await BalanceAsync(counter)).Should().Be(1);
        (await ReserveAsync(counter, 1)).Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task A_hold_still_inside_its_time_is_not_expired()
    {
        var counter = Fresh();
        await ReceiveAsync(counter, 1);
        await ReserveAsync(counter, 1, TimeSpan.FromMinutes(10));

        await using var sweep = host.OpenSession();
        await Stock.ReleaseExpiredAsync(sweep, DateTimeOffset.UtcNow.AddMinutes(1));
        await sweep.SaveChangesAsync();

        (await BalanceAsync(counter)).Should().Be(0);
    }

    [Fact]
    public async Task Expiry_runs_once_so_a_second_sweep_does_not_return_the_unit_twice()
    {
        var counter = Fresh();
        await ReceiveAsync(counter, 1);
        await ReserveAsync(counter, 1, TimeSpan.FromMinutes(1));
        var later = DateTimeOffset.UtcNow.AddMinutes(5);

        for (var i = 0; i < 2; i++)
        {
            await using var sweep = host.OpenSession();
            await Stock.ReleaseExpiredAsync(sweep, later);
            await sweep.SaveChangesAsync();
        }

        (await BalanceAsync(counter)).Should().Be(1);
    }

    [Fact]
    public async Task A_reservation_returns_expired_holds_on_its_counter_before_it_checks_the_floor()
    {
        var counter = Fresh();
        await ReceiveAsync(counter, 1);
        await ReserveAsync(counter, 1, TimeSpan.FromMilliseconds(1));
        await Task.Delay(50);

        (await ReserveAsync(counter, 1)).Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task A_confirmed_hold_keeps_its_unit_and_cannot_be_released()
    {
        var counter = Fresh();
        await ReceiveAsync(counter, 2);
        var hold = await ReserveAsync(counter, 1, TimeSpan.FromMinutes(10));

        await using (var session = host.OpenSession())
        {
            (await Stock.ConfirmAsync(session, hold.HoldId!.Value)).Should().BeTrue();
            (await Stock.ConfirmAsync(session, hold.HoldId!.Value)).Should().BeFalse();
            (await Stock.ReleaseAsync(session, hold.HoldId!.Value)).Should().BeFalse();
            await session.SaveChangesAsync();
        }

        await using (var sweep = host.OpenSession())
        {
            await Stock.ReleaseExpiredAsync(sweep, DateTimeOffset.UtcNow.AddHours(1));
            await sweep.SaveChangesAsync();
        }

        (await BalanceAsync(counter)).Should().Be(1);
    }

    [Fact]
    public async Task A_hold_past_its_expiry_cannot_be_confirmed_once_it_was_swept()
    {
        var counter = Fresh();
        await ReceiveAsync(counter, 1);
        var hold = await ReserveAsync(counter, 1, TimeSpan.FromMinutes(1));

        await using var session = host.OpenSession();
        await Stock.ReleaseExpiredAsync(session, DateTimeOffset.UtcNow.AddMinutes(2));
        (await Stock.ConfirmAsync(session, hold.HoldId!.Value)).Should().BeFalse();
        await session.SaveChangesAsync();

        (await BalanceAsync(counter)).Should().Be(1);
    }

    [Fact]
    public async Task A_released_hold_returns_its_unit_once()
    {
        var counter = Fresh();
        await ReceiveAsync(counter, 1);
        var hold = await ReserveAsync(counter, 1);

        await using (var session = host.OpenSession())
        {
            (await Stock.ReleaseAsync(session, hold.HoldId!.Value)).Should().BeTrue();
            (await Stock.ReleaseAsync(session, hold.HoldId!.Value)).Should().BeFalse();
            await session.SaveChangesAsync();
        }

        (await BalanceAsync(counter)).Should().Be(1);
    }

    [Fact]
    public async Task The_balance_is_the_sum_of_the_movements()
    {
        var counter = Fresh();
        await ReceiveAsync(counter, 10);
        var kept = await ReserveAsync(counter, 3, TimeSpan.FromMinutes(10));
        var dropped = await ReserveAsync(counter, 2);
        var lapsed = await ReserveAsync(counter, 1, TimeSpan.FromMinutes(1));
        await ReserveAsync(counter, 100);

        await using (var session = host.OpenSession())
        {
            await Stock.ConfirmAsync(session, kept.HoldId!.Value);
            await Stock.ReleaseAsync(session, dropped.HoldId!.Value);
            await Stock.ReleaseExpiredAsync(session, DateTimeOffset.UtcNow.AddMinutes(2));
            await session.SaveChangesAsync();
        }

        await using var read = host.OpenSession();
        var movements = await Stock.MovementsAsync(read, counter);

        movements.Should().HaveCount(7);
        movements.Select(m => m.Kind).Should().BeEquivalentTo(new[]
        {
            StockMovementKind.Received, StockMovementKind.Reserved, StockMovementKind.Reserved, StockMovementKind.Reserved,
            StockMovementKind.Confirmed, StockMovementKind.Released, StockMovementKind.Expired,
        });
        movements.Single(m => m.Kind == StockMovementKind.Expired).HoldId.Should().Be(lapsed.HoldId);
        movements.Sum(m => m.Delta).Should().Be(await Stock.BalanceAsync(read, counter));
        movements.Sum(m => m.Delta).Should().Be(7);
    }

    [Fact]
    public async Task Counters_are_per_tenant()
    {
        var counter = Fresh();
        var tenant = "stock-" + Guid.NewGuid().ToString("n")[..8];
        await ReceiveAsync(counter, 1);

        (await ReserveAsync(counter, 1, tenant: tenant)).Succeeded.Should().BeFalse();
        (await BalanceAsync(counter, tenant)).Should().BeNull();
        (await ReserveAsync(counter, 1)).Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task A_quantity_of_zero_or_less_is_refused_as_a_mistake_not_a_result()
    {
        await using var session = host.OpenSession();

        var zero = async () => await Stock.ReserveAsync(session, Fresh(), 0);
        var negative = async () => await Stock.ReceiveAsync(session, Fresh(), -1);

        await zero.Should().ThrowAsync<ArgumentOutOfRangeException>();
        await negative.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task A_lapsed_hold_counts_in_the_balance_without_any_sweep()
    {
        var counter = Fresh();
        await ReceiveAsync(counter, 1);
        (await ReserveAsync(counter, 1, TimeSpan.FromMilliseconds(1))).Succeeded.Should().BeTrue();
        await Task.Delay(100);

        (await BalanceAsync(counter)).Should().Be(1);
    }

    [Fact]
    public async Task A_hold_cannot_be_confirmed_after_it_lapsed_even_before_any_sweep()
    {
        var counter = Fresh();
        await ReceiveAsync(counter, 1);
        var hold = await ReserveAsync(counter, 1, TimeSpan.FromMilliseconds(1));
        await Task.Delay(100);

        await using (var session = host.OpenSession())
        {
            (await Stock.ConfirmAsync(session, hold.HoldId!.Value)).Should().BeFalse();
            await session.SaveChangesAsync();
        }

        await using (var sweep = host.OpenSession())
        {
            (await Stock.ReleaseExpiredAsync(sweep)).Should().BeGreaterThanOrEqualTo(1);
            await sweep.SaveChangesAsync();
        }

        (await BalanceAsync(counter)).Should().Be(1);
    }

    [Fact]
    public async Task A_sweep_given_a_time_with_an_offset_expires_the_same_holds()
    {
        var counter = Fresh();
        await ReceiveAsync(counter, 1);
        await ReserveAsync(counter, 1, TimeSpan.FromMilliseconds(1));
        await Task.Delay(100);

        await using (var sweep = host.OpenSession())
        {
            var at = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8));
            (await Stock.ReleaseExpiredAsync(sweep, at)).Should().BeGreaterThanOrEqualTo(1);
            await sweep.SaveChangesAsync();
        }

        (await BalanceAsync(counter)).Should().Be(1);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(int.MaxValue)]
    public async Task A_hold_longer_than_the_cap_is_refused_as_an_argument(int days)
    {
        var counter = Fresh();
        await ReceiveAsync(counter, 1);
        await using var session = host.OpenSession();
        var holdFor = days == int.MaxValue ? TimeSpan.MaxValue : TimeSpan.FromDays(days);

        var act = () => Stock.ReserveAsync(session, counter, 1, holdFor);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>().WithParameterName("holdFor");
    }

    [Fact]
    public async Task Another_tenant_cannot_confirm_or_release_a_hold()
    {
        var counter = Fresh();
        var owner = $"tenant-{Guid.NewGuid():N}"[..16];
        var other = $"tenant-{Guid.NewGuid():N}"[..16];
        await ReceiveAsync(counter, 1, tenant: owner);
        var hold = await ReserveAsync(counter, 1, TimeSpan.FromMinutes(10), owner);
        hold.Succeeded.Should().BeTrue();

        await using (var session = host.OpenSession(other))
        {
            (await Stock.ConfirmAsync(session, hold.HoldId!.Value)).Should().BeFalse();
            (await Stock.ReleaseAsync(session, hold.HoldId!.Value)).Should().BeFalse();
            await session.SaveChangesAsync();
        }

        (await BalanceAsync(counter, owner)).Should().Be(0);
    }

    [Fact]
    public async Task The_sweeper_returns_a_lapsed_hold_on_a_counter_nobody_reserves_again()
    {
        var counter = Fresh();
        await ReceiveAsync(counter, 1);
        var hold = await ReserveAsync(counter, 1, TimeSpan.FromMilliseconds(1));
        await Task.Delay(100);
        var sweeper = new StockHoldSweeper(
            host.Services.GetRequiredService<IDocumentStore>(), host.Services.GetRequiredService<IConfiguration>(),
            Stock, NullLogger<StockHoldSweeper>.Instance);

        (await sweeper.SweepAsync(DateTimeOffset.UtcNow, default)).Should().BeGreaterThanOrEqualTo(1);

        await using var read = host.OpenSession();
        var movements = await Stock.MovementsAsync(read, counter);
        movements.Should().NotBeEmpty();
        movements.Should().ContainSingle(m => m.Kind == StockMovementKind.Expired && m.HoldId == hold.HoldId);
        (await BalanceAsync(counter)).Should().Be(1);
    }

    [Fact]
    public async Task Movements_with_the_same_timestamp_read_in_the_order_they_happened()
    {
        var counter = Fresh();
        var stock = new StockReservations(new FixedClock(DateTimeOffset.UtcNow));

        await using (var session = host.OpenSession())
        {
            await stock.ReceiveAsync(session, counter, 5);
            var hold = await stock.ReserveAsync(session, counter, 2);
            await stock.ReleaseAsync(session, hold.HoldId!.Value);
            await session.SaveChangesAsync();
        }

        await using var read = host.OpenSession();
        var movements = await stock.MovementsAsync(read, counter);

        movements.Should().HaveCount(3);
        movements.Select(m => m.Kind).Should().Equal(StockMovementKind.Received, StockMovementKind.Reserved, StockMovementKind.Released);
    }

    [Fact]
    public async Task A_reference_longer_than_the_cap_is_refused_as_an_argument()
    {
        await using var session = host.OpenSession();
        var tooLong = new string('x', 257);

        var receive = async () => await Stock.ReceiveAsync(session, Fresh(), 1, tooLong);
        var reserve = async () => await Stock.ReserveAsync(session, Fresh(), 1, null, tooLong);

        await receive.Should().ThrowAsync<ArgumentException>().WithParameterName("reference");
        await reserve.Should().ThrowAsync<ArgumentException>().WithParameterName("reference");
    }

    [Fact]
    public async Task An_expiry_and_the_reservation_that_follows_it_read_in_that_order()
    {
        var counter = Fresh();
        var start = DateTimeOffset.UtcNow;
        var clock = new FixedClock(start);
        var stock = new StockReservations(clock);

        await using (var first = host.OpenSession())
        {
            await stock.ReceiveAsync(first, counter, 1);
            (await stock.ReserveAsync(first, counter, 1, TimeSpan.FromMinutes(1))).Succeeded.Should().BeTrue();
            await first.SaveChangesAsync();
        }

        clock.Now = start.AddMinutes(5);
        await using (var second = host.OpenSession())
        {
            (await stock.ReserveAsync(second, counter, 1)).Succeeded.Should().BeTrue();
            await second.SaveChangesAsync();
        }

        await using var read = host.OpenSession();
        var movements = await stock.MovementsAsync(read, counter);

        movements.Should().HaveCount(4);
        movements.Select(m => m.Kind).Should().Equal(
            StockMovementKind.Received, StockMovementKind.Reserved, StockMovementKind.Expired, StockMovementKind.Reserved);
    }

    [Fact]
    public async Task Releasing_a_lapsed_hold_before_any_sweep_expires_it_and_returns_false()
    {
        var counter = Fresh();
        await ReceiveAsync(counter, 1);
        var hold = await ReserveAsync(counter, 1, TimeSpan.FromMilliseconds(1));
        await Task.Delay(100);

        await using (var session = host.OpenSession())
        {
            (await Stock.ReleaseAsync(session, hold.HoldId!.Value)).Should().BeFalse();
            await session.SaveChangesAsync();
        }

        await using var read = host.OpenSession();
        var movements = await Stock.MovementsAsync(read, counter);
        movements.Should().NotBeEmpty();
        movements.Select(m => m.Kind).Should().NotContain(StockMovementKind.Released);
        movements.Should().ContainSingle(m => m.Kind == StockMovementKind.Expired && m.HoldId == hold.HoldId);
        (await BalanceAsync(counter)).Should().Be(1);
    }

    [Fact]
    public async Task One_tenant_failing_does_not_stop_the_sweep_of_the_others()
    {
        var tenants = new[] { $"sweep-{Guid.NewGuid():N}"[..16], $"sweep-{Guid.NewGuid():N}"[..16] };
        foreach (var tenant in tenants)
        {
            var counter = Fresh();
            await ReceiveAsync(counter, 1, tenant: tenant);
            await ReserveAsync(counter, 1, TimeSpan.FromMilliseconds(1), tenant);
        }
        await Task.Delay(100);
        var failing = new FailsFirstSweep(Stock);
        var sweeper = new StockHoldSweeper(
            host.Services.GetRequiredService<IDocumentStore>(), host.Services.GetRequiredService<IConfiguration>(),
            failing, NullLogger<StockHoldSweeper>.Instance);

        var expired = await sweeper.SweepAsync(DateTimeOffset.UtcNow, default);

        failing.Calls.Should().BeGreaterThanOrEqualTo(2);
        expired.Should().BeGreaterThanOrEqualTo(1);
    }

    private sealed class FailsFirstSweep(IStockReservations inner) : IStockReservations
    {
        public int Calls { get; private set; }

        public Task<int> ReleaseExpiredAsync(IDocumentSession session, DateTimeOffset? at = null, CancellationToken ct = default) =>
            ++Calls == 1 ? throw new InvalidOperationException("first tenant fails") : inner.ReleaseExpiredAsync(session, at, ct);

        public Task<long> ReceiveAsync(IDocumentSession session, string counter, long quantity, string reference = "", long floor = 0, CancellationToken ct = default) =>
            inner.ReceiveAsync(session, counter, quantity, reference, floor, ct);

        public Task<StockReservation> ReserveAsync(IDocumentSession session, string counter, long quantity, TimeSpan? holdFor = null, string reference = "", CancellationToken ct = default) =>
            inner.ReserveAsync(session, counter, quantity, holdFor, reference, ct);

        public Task<bool> ConfirmAsync(IDocumentSession session, Guid holdId, CancellationToken ct = default) => inner.ConfirmAsync(session, holdId, ct);

        public Task<bool> ReleaseAsync(IDocumentSession session, Guid holdId, CancellationToken ct = default) => inner.ReleaseAsync(session, holdId, ct);

        public Task<long?> BalanceAsync(IQuerySession session, string counter, CancellationToken ct = default) => inner.BalanceAsync(session, counter, ct);

        public Task<IReadOnlyList<StockMovement>> MovementsAsync(IQuerySession session, string counter, CancellationToken ct = default) =>
            inner.MovementsAsync(session, counter, ct);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
