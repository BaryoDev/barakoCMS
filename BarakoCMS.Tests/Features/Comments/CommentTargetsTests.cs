using System.Security.Claims;
using BarakoCMS.Comments;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests.Features.Comments;

public class CommentTargetsTests
{
    private sealed class Named(string type) : ICommentTarget
    {
        public string Type => type;

        public Task<CommentTargetAccess> CheckReadAsync(Guid targetId, ClaimsPrincipal caller, CancellationToken ct) =>
            Task.FromResult(CommentTargetAccess.Allowed);
    }

    [Fact]
    public void Two_targets_registering_the_same_type_fail_instead_of_one_winning()
    {
        var build = () => new CommentTargets([new Named("ticket"), new Named("TICKET")]);

        build.Should().Throw<InvalidOperationException>().WithMessage("*ticket*");
    }

    [Fact]
    public void Distinct_types_are_found_by_name_without_regard_to_case()
    {
        var ticket = new Named("ticket");
        var targets = new CommentTargets([new Named("content"), ticket]);

        targets.Find(" Ticket ").Should().BeSameAs(ticket);
        targets.Find("nothing").Should().BeNull();
    }
}
