using FastEndpoints;
using FluentAssertions;
using Xunit;
using NSubstitute;
using barakoCMS.Models;
using barakoCMS.Repository;
using Marten;
using Marten.Events;
using Microsoft.Extensions.Configuration;

namespace BarakoCMS.Tests;

public class UnitTests
{
    [Fact(Skip = "Covered by Integration Tests")]
    public async Task Register_Should_Succeed_When_Valid()
    {
        // Arrange
        var repo = Substitute.For<IUserRepository>();
        var session = Substitute.For<IQuerySession>();

        repo.GetByUsernameOrEmailAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<User?>(null));

        // Mock Role Query
        // This is hard to mock with NSubstitute for Marten's Query<T> extension methods.
        // We will skip strict role checking in this unit test or assume it returns null/default.
        // The endpoint logic: var userRole = await _session.Query<Role>().FirstOrDefaultAsync(r => r.Name == "User", ct);
        // We can't easily mock extension methods. 
        // Ideally, we should wrap Role retrieval in a service/repo.
        // For now, we will just verify Store is called.

        var endpoint = Factory.Create<barakoCMS.Features.Auth.Register.Endpoint>(repo, session);
        var req = new barakoCMS.Features.Auth.Register.Request
        {
            Username = "testuser",
            Email = "test@test.com",
            Password = "Password123!"
        };

        // Act
        await endpoint.HandleAsync(req, CancellationToken.None);

        // Assert
        endpoint.ValidationFailed.Should().BeFalse();
        repo.Received(1).Store(Arg.Is<User>(u => u.Username == "testuser"));
        await repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Register_Should_Fail_When_User_Exists()
    {
        // Arrange
        var repo = Substitute.For<IUserRepository>();
        var session = Substitute.For<IQuerySession>();
        var passwordValidator = new barakoCMS.Infrastructure.Services.PasswordPolicyValidator();

        repo.GetByUsernameOrEmailAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new User { Username = "existinguser" });

        var endpoint = Factory.Create<barakoCMS.Features.Auth.Register.Endpoint>(repo, session, passwordValidator);
        var req = new barakoCMS.Features.Auth.Register.Request
        {
            Username = "existinguser",
            Email = "existing@test.com",
            Password = "ValidPassword123!"
        };

        // Act
        try
        {
            await endpoint.HandleAsync(req, CancellationToken.None);
        }
        catch (ValidationFailureException) { }

        // Assert
        endpoint.ValidationFailed.Should().BeTrue();
        repo.DidNotReceive().Store(Arg.Any<User>());
    }

    // Login test is commented out because mocking Marten Query for Roles is difficult without a wrapper.
    // Integration tests cover this.
    /*
    [Fact]
    public async Task Login_Should_Succeed_With_Valid_Credentials()
    {
        // ...
    }
    */
}
