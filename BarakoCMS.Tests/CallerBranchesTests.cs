using barakoCMS.Infrastructure.Services;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests;

public class CallerBranchesTests
{
    private static string Ids(int count) => string.Join(",", Enumerable.Range(0, count).Select(_ => Guid.NewGuid()));

    [Fact]
    public void Parse_reads_a_comma_list_and_ignores_spaces_around_parts()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        CallerBranches.Parse($" {a} ,{b} ").Should().Equal(a, b);
    }

    [Fact]
    public void Parse_gives_an_empty_list_when_any_part_is_not_a_guid()
    {
        CallerBranches.Parse($"{Guid.NewGuid()},br1").Should().BeEmpty();
        CallerBranches.Parse(null).Should().BeEmpty();
        CallerBranches.Parse("").Should().BeEmpty();
    }

    [Fact]
    public void WithRequestBranch_sets_branch_only_for_a_listed_branch()
    {
        var mine = Guid.NewGuid();
        var profile = new Dictionary<string, string> { ["branches"] = mine.ToString() };

        CallerBranches.WithRequestBranch(profile, mine)!["branch"].Should().Be(mine.ToString());
        CallerBranches.WithRequestBranch(profile, Guid.NewGuid())!.ContainsKey("branch").Should().BeFalse();
        CallerBranches.WithRequestBranch(profile, null)!.ContainsKey("branch").Should().BeFalse();
    }

    [Fact]
    public void WithRequestBranch_does_not_change_the_stored_profile()
    {
        var mine = Guid.NewGuid();
        var profile = new Dictionary<string, string> { ["branches"] = mine.ToString() };

        CallerBranches.WithRequestBranch(profile, mine);

        profile.Should().ContainSingle().Which.Key.Should().Be("branches");
    }

    [Fact]
    public void ProfileError_refuses_a_stored_branch_attribute()
    {
        CallerAttributes.ProfileError(new Dictionary<string, string> { ["branch"] = Guid.NewGuid().ToString() })
            .Should().Be("The profile attribute 'branch' is set by the request and cannot be stored.");
    }

    [Fact]
    public void ProfileError_refuses_33_branches()
    {
        CallerAttributes.ProfileError(new Dictionary<string, string> { ["branches"] = Ids(33) })
            .Should().Be("The profile attribute 'branches' must be up to 32 branch ids separated by commas.");
    }

    [Fact]
    public void ProfileError_accepts_32_branches()
    {
        var value = Ids(32);
        value.Length.Should().Be(1183);
        CallerAttributes.ProfileError(new Dictionary<string, string> { ["branches"] = value }).Should().BeNull();
    }
}
