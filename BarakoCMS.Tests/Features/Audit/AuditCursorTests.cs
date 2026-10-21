using barakoCMS.Features.Audit.List;
using barakoCMS.Models;
using FluentAssertions;

namespace BarakoCMS.Tests.Features.Audit;

/// <summary>Where the audit cursor goes after a page, with no database in the way.</summary>
public class AuditCursorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    private static AuditCursor Input(TimeSpan before) => new(Now - before, Guid.Empty);

    private static List<AuditEvent> Page(params TimeSpan[] before) =>
        before.Select(b => new AuditEvent { Id = Guid.NewGuid(), CreatedAt = (Now - b).UtcDateTime }).ToList();

    [Fact]
    public void A_page_older_than_the_margin_returns_its_last_entry_exactly()
    {
        var page = Page(TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(1));

        var next = AuditCursor.Next(Input(TimeSpan.FromHours(1)), page, hasMore: true, Now);

        next.Should().Be(new AuditCursor(new DateTimeOffset(page[^1].CreatedAt, TimeSpan.Zero), page[^1].Id));
    }

    [Fact]
    public void An_exact_cursor_on_a_seventh_digit_of_five_is_cut_not_rounded()
    {
        var at = Now.AddHours(-1).UtcDateTime.AddTicks(5);
        var page = new List<AuditEvent> { new() { Id = Guid.NewGuid(), CreatedAt = at } };

        var next = AuditCursor.Next(Input(TimeSpan.FromHours(2)), page, hasMore: true, Now);

        next.At.UtcTicks.Should().Be(at.Ticks - 5, "a tie must never land above the stored value");
    }

    [Fact]
    public void An_exact_cursor_cuts_a_fraction_above_half_a_microsecond_down_too()
    {
        var at = Now.AddHours(-1).UtcDateTime.AddTicks(7);
        var page = new List<AuditEvent> { new() { Id = Guid.NewGuid(), CreatedAt = at } };

        var next = AuditCursor.Next(Input(TimeSpan.FromHours(2)), page, hasMore: true, Now);

        next.At.UtcTicks.Should().Be(at.Ticks - 7);
    }

    [Fact]
    public void A_stamp_is_cut_at_the_microsecond_and_keeps_its_kind()
    {
        var at = new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc).AddTicks(1234565);

        var stamp = AuditCursor.TruncateToMicroseconds(at);

        stamp.Ticks.Should().Be(at.Ticks - 5);
        stamp.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public void An_exact_cursor_cuts_a_fraction_under_half_a_microsecond_down()
    {
        var at = Now.AddHours(-1).UtcDateTime.AddTicks(3);
        var page = new List<AuditEvent> { new() { Id = Guid.NewGuid(), CreatedAt = at } };

        var next = AuditCursor.Next(Input(TimeSpan.FromHours(2)), page, hasMore: true, Now);

        next.At.UtcTicks.Should().Be(at.Ticks - 3);
    }

    [Fact]
    public void A_page_inside_the_margin_lags_so_a_late_commit_is_read_on_the_next_poll()
    {
        var page = Page(TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(1));

        var next = AuditCursor.Next(Input(TimeSpan.FromHours(1)), page, hasMore: false, Now);

        next.At.Should().Be(Now - AuditCursor.Margin);
        next.Id.Should().Be(Guid.Empty);
    }

    [Fact]
    public void A_page_wholly_inside_the_margin_with_more_behind_it_still_moves_forward()
    {
        var page = Page(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(2));
        var input = Input(TimeSpan.FromSeconds(5));

        var next = AuditCursor.Next(input, page, hasMore: true, Now);

        next.Should().NotBe(input, "lagging would read this page again for ever");
        next.Id.Should().Be(page[^1].Id);
    }

    [Fact]
    public void An_empty_page_returns_the_cursor_it_was_given()
    {
        var input = Input(TimeSpan.FromMinutes(1));

        AuditCursor.Next(input, [], hasMore: false, Now).Should().Be(input);
    }

    [Fact]
    public void A_cursor_round_trips_through_its_text_and_a_timestamp_is_a_first_cursor()
    {
        var cursor = new AuditCursor(Now, Guid.NewGuid());

        AuditCursor.TryParse(cursor.ToString(), out var parsed).Should().BeTrue();
        parsed.Should().Be(cursor);
        AuditCursor.TryParse("2026-10-21T00:00:00Z", out var first).Should().BeTrue();
        first.Id.Should().Be(Guid.Empty);
        AuditCursor.TryParse("not a cursor", out _).Should().BeFalse();
        AuditCursor.TryParse(null, out _).Should().BeFalse();
    }
}
