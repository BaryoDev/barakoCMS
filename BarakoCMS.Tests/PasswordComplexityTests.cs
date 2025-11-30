using FastEndpoints;
using FluentAssertions;
using Xunit;
using NSubstitute;
using barakoCMS.Models;
using barakoCMS.Repository;
using Marten;

namespace BarakoCMS.Tests;

public class PasswordComplexityTests
{
    [Fact]
    public async Task Register_Should_Fail_With_Weak_Password()
    {
        // Arrange
        var repo = Substitute.For<IUserRepository>();
        var session = Substitute.For<IQuerySession>();

        repo.GetByUsernameOrEmailAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<User?>(null));

        var endpoint = Factory.Create<barakoCMS.Features.Auth.Register.Endpoint>(repo, session);
        var req = new barakoCMS.Features.Auth.Register.Request
        {
            Username = "weakuser",
            Email = "weak@test.com",
            Password = "weak" // Too short, no complexity
        };

        // Act
        try
        {
            await endpoint.HandleAsync(req, CancellationToken.None);
        }
        catch (ValidationFailureException) { }

        // Assert
        endpoint.ValidationFailed.Should().BeTrue();
        endpoint.ValidationFailures.Should().Contain(f => f.ErrorMessage.Contains("Password must be at least 8 characters long"));
    }

    [Fact]
    public async Task Register_Should_Succeed_With_Strong_Password_Unit()
    {
        // Arrange
        var repo = Substitute.For<IUserRepository>();
        var session = Substitute.For<IQuerySession>();

        repo.GetByUsernameOrEmailAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<User?>(null));

        var endpoint = Factory.Create<barakoCMS.Features.Auth.Register.Endpoint>(repo, session);
        var req = new barakoCMS.Features.Auth.Register.Request
        {
            Username = "stronguser",
            Email = "strong@test.com",
            Password = "Password123!"
        };

        // Act
        try
        {
            await endpoint.HandleAsync(req, CancellationToken.None);
        }
        catch (Exception ex)
        {
            if (ex is ValidationFailureException) throw;
            // Ignore other errors (like Role mock failure) which happen after validation
        }

        // Assert
        endpoint.ValidationFailed.Should().BeFalse();
    }
}
