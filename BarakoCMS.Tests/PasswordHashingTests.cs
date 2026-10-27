using barakoCMS.Infrastructure.Auth;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// <see cref="PasswordHashing.Verify"/> answers false for anything that is not a matching hash and
/// never throws, so a bad stored value is a refusal and not a server error.
/// </summary>
public class PasswordHashingTests
{
    private const string Password = "ValidPassword123!";

    [Fact]
    public void A_real_hash_matches_its_password_and_no_other()
    {
        var hash = PasswordHashing.Hash(Password);

        PasswordHashing.Verify(Password, hash).Should().BeTrue();
        PasswordHashing.Verify("SomeOtherPassword456!", hash).Should().BeFalse();
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData(null, "$2a$11$abcdefghijklmnopqrstuuabcdefghijklmnopqrstuvwxyz01234")]
    [InlineData("", "$2a$11$abcdefghijklmnopqrstuuabcdefghijklmnopqrstuvwxyz01234")]
    [InlineData(Password, null)]
    [InlineData(Password, "")]
    [InlineData(Password, "abc")]
    [InlineData(Password, "$2a$")]
    [InlineData(Password, "$2a$11$short")]
    public void Empty_or_malformed_input_is_false_and_does_not_throw(string? password, string? hash)
    {
        var act = () => PasswordHashing.Verify(password, hash);

        act.Should().NotThrow();
        act().Should().BeFalse();
    }
}
