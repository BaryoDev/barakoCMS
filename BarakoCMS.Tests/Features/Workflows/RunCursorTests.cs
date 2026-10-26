using barakoCMS.Features.WorkflowRuns;
using barakoCMS.Models;
using FluentAssertions;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// Where the changed-since cursor goes after a page, with no database in the way.
/// </summary>
public class RunCursorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    private static RunCursor Input(TimeSpan before) => new(Now - before, Guid.Empty);

    private static List<WorkflowRun> Page(params TimeSpan[] before) =>
        before.Select(b => new WorkflowRun { Id = Guid.NewGuid(), UpdatedAt = Now - b }).ToList();

    [Fact]
    public void A_page_with_more_behind_it_moves_on_and_anchors_at_the_margin_when_its_last_run_is_inside_it()
    {
        var page = Page(TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(1));

        var next = RunCursor.Next(Input(TimeSpan.FromHours(1)), page, hasMore: true, Now);

        next.At.Should().Be(page[^1].UpdatedAt, "lagging would send every page back to the same runs");
        next.Id.Should().Be(page[^1].Id);
        next.Anchor.Should().Be(Now - RunCursor.Margin, "a run stamped before the last row may still commit");
    }

    [Fact]
    public void A_later_page_keeps_the_anchor_it_was_given()
    {
        var anchor = Now - TimeSpan.FromSeconds(9);
        var input = new RunCursor(Now - TimeSpan.FromSeconds(4), Guid.NewGuid(), anchor);
        var page = Page(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(2));

        RunCursor.Next(input, page, hasMore: true, Now).Anchor.Should().Be(anchor);
    }

    [Fact]
    public void The_last_page_of_an_anchored_run_returns_the_anchor()
    {
        var anchor = Now - TimeSpan.FromSeconds(9);
        var input = new RunCursor(Now - TimeSpan.FromSeconds(4), Guid.NewGuid(), anchor);
        var page = Page(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(2));

        RunCursor.Next(input, page, hasMore: false, Now).Should().Be(new RunCursor(anchor, Guid.Empty));
    }

    [Fact]
    public void A_cursor_with_an_anchor_reads_back_as_itself()
    {
        var cursor = new RunCursor(Now, Guid.NewGuid(), Now - RunCursor.Margin);

        RunCursor.TryParse(cursor.ToString(), out var read).Should().BeTrue();
        read.Should().Be(cursor);
    }

    [Fact]
    public void A_cursor_issued_before_the_anchor_existed_still_reads()
    {
        var id = Guid.NewGuid();
        var issued = $"{Now.UtcTicks}_{id:N}";

        RunCursor.TryParse(issued, out var read).Should().BeTrue();
        read.Should().Be(new RunCursor(Now, id));
        read.ToString().Should().Be(issued, "a cursor without an anchor keeps the 5.3 form");
    }

    [Fact]
    public void A_page_with_more_behind_it_returns_its_last_run_when_that_run_is_older_than_the_margin()
    {
        var page = Page(TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(1));

        var next = RunCursor.Next(Input(TimeSpan.FromHours(1)), page, hasMore: true, Now);

        next.Should().Be(new RunCursor(page[^1].UpdatedAt, page[^1].Id));
    }

    [Fact]
    public void A_page_wholly_inside_the_margin_with_more_behind_it_still_moves_the_cursor_forward()
    {
        var page = Page(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1));
        var input = Input(TimeSpan.FromSeconds(5));

        var next = RunCursor.Next(input, page, hasMore: true, Now);

        next.At.Should().Be(page[^1].UpdatedAt, "lagging would read this page again for ever");
        next.Id.Should().Be(page[^1].Id);
        next.Anchor.Should().Be(input.At);
    }

    [Fact]
    public void The_last_page_inside_the_margin_keeps_the_input_when_the_margin_is_not_past_it()
    {
        var page = Page(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1));
        var input = Input(TimeSpan.FromSeconds(4));

        RunCursor.Next(input, page, hasMore: false, Now).Should().Be(input);
    }

    [Fact]
    public void The_last_page_inside_the_margin_lags_when_the_margin_is_past_the_input()
    {
        var page = Page(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1));

        var next = RunCursor.Next(Input(TimeSpan.FromHours(1)), page, hasMore: false, Now);

        next.At.Should().Be(Now - RunCursor.Margin);
    }

    [Fact]
    public void An_empty_page_returns_the_cursor_it_was_given()
    {
        var input = Input(TimeSpan.FromMinutes(1));

        RunCursor.Next(input, [], hasMore: false, Now).Should().Be(input);
    }
}
