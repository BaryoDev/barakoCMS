using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Numbering;
using BarakoCMS.Tests.Clinic;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Numbering;

[Collection("Sequential")]
public class NumberAllocatorTests(ClinicProbeHost host) : IClassFixture<ClinicProbeHost>
{
    private INumberAllocator Allocator => host.Services.GetRequiredService<INumberAllocator>();

    private static NumberSeries Fresh(NumberReset reset = NumberReset.Never) =>
        new($"t-{Guid.NewGuid():N}"[..20], Reset: reset);

    private async Task<AllocatedNumber> TakeAsync(NumberSeries series, string? tenant = null, INumberAllocator? allocator = null)
    {
        await using var session = host.OpenSession(tenant);
        var number = await (allocator ?? Allocator).NextAsync(session, series);
        await session.SaveChangesAsync();
        return number;
    }

    [Fact]
    public async Task Allocator_FiftyParallelWriters_NoDuplicatesNoGaps()
    {
        var series = Fresh();

        var numbers = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => TakeAsync(series)));

        numbers.Select(n => n.Value).Order().Should().Equal(Enumerable.Range(1, 50).Select(i => (long)i));
    }

    [Fact]
    public async Task Allocator_SessionDisposedWithoutSave_ReusesNumber()
    {
        var series = Fresh();

        await using (var abandoned = host.OpenSession())
        {
            (await Allocator.NextAsync(abandoned, series)).Value.Should().Be(1);
            // No SaveChangesAsync: the transaction rolls back on dispose.
        }

        (await TakeAsync(series)).Value.Should().Be(1);
    }

    [Fact]
    public async Task Allocator_NumberCommitsWithTheDocumentItNames()
    {
        var series = Fresh();
        var id = Guid.NewGuid();

        await using (var session = host.OpenSession())
        {
            var number = await Allocator.NextAsync(session, series);
            session.Events.StartStream(id, new ProbeTicketIssued(id, number.Formatted));
            await session.SaveChangesAsync();
        }

        await using var read = host.OpenSession();
        (await read.LoadAsync<ProbeTicket>(id))!.Label.Should().Be("00001");
    }

    [Fact]
    public async Task Allocator_TwoNumbersInOneSession_AreConsecutive()
    {
        var series = Fresh();

        await using var session = host.OpenSession();
        var first = await Allocator.NextAsync(session, series);
        var second = await Allocator.NextAsync(session, series);
        await session.SaveChangesAsync();

        (first.Value, second.Value).Should().Be((1L, 2L));
    }

    [Fact]
    public async Task Allocator_SaveWithNothingElse_KeepsTheNumber()
    {
        var series = Fresh();

        (await TakeAsync(series)).Value.Should().Be(1);
        (await TakeAsync(series)).Value.Should().Be(2);
    }

    [Fact]
    public async Task Allocator_ThenGuidVersionedWriteThenNumericRevisionWrite_BothCommit()
    {
        var series = Fresh();
        var guarded = Guid.NewGuid();
        var revisioned = Guid.NewGuid();

        await using (var session = host.OpenSession())
        {
            var number = await Allocator.NextAsync(session, series);
            session.Store(new ProbeGuarded { Id = guarded, Note = number.Formatted });
            session.Store(new ProbeRevisioned { Id = revisioned, Note = number.Formatted });
            await session.SaveChangesAsync();
        }

        await using var read = host.OpenSession();
        (await read.LoadAsync<ProbeGuarded>(guarded))!.Note.Should().Be("00001");
        (await read.LoadAsync<ProbeRevisioned>(revisioned))!.Note.Should().Be("00001");
        (await TakeAsync(series)).Value.Should().Be(2);
    }

    [Fact]
    public async Task Allocator_TwoTenants_CountSeparately()
    {
        var series = Fresh();
        var tenant = await host.CreateTenantAsync();

        (await TakeAsync(series)).Value.Should().Be(1);
        (await TakeAsync(series)).Value.Should().Be(2);
        (await TakeAsync(series, tenant)).Value.Should().Be(1);
        (await TakeAsync(series, tenant)).Value.Should().Be(2);
    }

    [Fact]
    public async Task Allocator_SecondSessionOnSameSeries_WaitsForFirstToEnd()
    {
        var series = Fresh();

        var first = host.OpenSession();
        (await Allocator.NextAsync(first, series)).Value.Should().Be(1);

        var second = host.OpenSession();
        var pending = Allocator.NextAsync(second, series);

        var finishedEarly = await Task.WhenAny(pending, Task.Delay(500)) == pending;
        finishedEarly.Should().BeFalse("the counter row stays locked until the first session ends");

        await first.DisposeAsync();
        (await pending).Value.Should().Be(1);
        await second.DisposeAsync();
    }

    [Fact]
    public async Task Allocator_DailySeries_RestartsOnNextLocalDay()
    {
        var series = Fresh(NumberReset.Daily) with { TimeZoneId = "Asia/Manila" };
        var clock = new FixedClock(new DateTimeOffset(2026, 10, 11, 15, 0, 0, TimeSpan.Zero));
        var allocator = new GaplessNumberAllocator(clock);

        (await TakeAsync(series, allocator: allocator)).Formatted.Should().Be("2026-10-11-00001");
        (await TakeAsync(series, allocator: allocator)).Value.Should().Be(2);

        clock.Now = new DateTimeOffset(2026, 10, 11, 16, 0, 0, TimeSpan.Zero); // Manila midnight
        (await TakeAsync(series, allocator: allocator)).Formatted.Should().Be("2026-10-12-00001");
    }

    [Fact]
    public async Task Allocator_InvalidSeries_ThrowsBeforeTouchingTheDatabase()
    {
        await using var session = host.OpenSession();
        var act = () => Allocator.NextAsync(session, new NumberSeries("Bad Name"));
        await act.Should().ThrowAsync<ArgumentException>();
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
