using barakoCMS.Features.WorkflowRuns;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// The run list's ETag has to change when the cap or the capped statuses do, or a client gets a 304 with stale ones.
/// </summary>
public class RunListETagTests
{
    private static string Tag(int? cap, string[]? capped)
    {
        var counts = new Dictionary<string, int> { ["Succeeded"] = 1000 };
        return RunListETag.Compute(new DefaultHttpContext(), [], 1, 20, 0, counts, cap, capped, "x");
    }

    [Fact]
    public void A_status_going_past_the_cap_changes_the_tag_though_the_counts_do_not()
    {
        Tag(1000, []).Should().NotBe(Tag(1000, ["Succeeded"]));
    }

    [Fact]
    public void A_different_cap_changes_the_tag()
    {
        Tag(1000, []).Should().NotBe(Tag(500, []));
    }

    [Fact]
    public void The_same_inputs_give_the_same_tag()
    {
        Tag(1000, ["Succeeded"]).Should().Be(Tag(1000, ["Succeeded"]));
    }
}
