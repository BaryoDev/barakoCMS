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
    public void A_page_with_more_behind_it_lags_when_its_last_run_is_inside_the_margin()
    {
        var page = Page(TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(1));

        var next = RunCursor.Next(Input(TimeSpan.FromHours(1)), page, hasMore: true, Now);

        next.At.Should().Be(Now - RunCursor.Margin, "a run stamped before the last row may still commit");
        next.Id.Should().Be(Guid.Empty);
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

        next.Should().Be(new RunCursor(page[^1].UpdatedAt, page[^1].Id), "lagging would read this page again for ever");
        next.Should().NotBe(input);
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
