using FastEndpoints;
using FluentAssertions;
using Xunit;
using NSubstitute;
using barakoCMS.Models;
using barakoCMS.Repository;
using Marten;
using Marten.Events;

namespace BarakoCMS.Tests;

public class UnitTests
{
    [Fact]
    public async Task Register_Should_Succeed_When_Valid()
    {
        // Arrange
        var repo = Substitute.For<IUserRepository>();
        repo.GetByUsernameOrEmailAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<User?>(null));

        var endpoint = Factory.Create<barakoCMS.Features.Auth.Register.Endpoint>(repo);
        var req = new barakoCMS.Features.Auth.Register.Request
        {
            Username = "testuser",
            Email = "test@test.com",
            Password = "password123"
        };

        // Act
        await endpoint.HandleAsync(req, CancellationToken.None);

        // Assert
        endpoint.ValidationFailed.Should().BeFalse();
        repo.Received(1).Store(Arg.Is<User>(u => u.Username == "testuser" && u.Role == "User"));
        await repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Register_Should_Fail_When_User_Exists()
    {
        // Arrange
        var repo = Substitute.For<IUserRepository>();
        repo.GetByUsernameOrEmailAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<User?>(new User()));

        var endpoint = Factory.Create<barakoCMS.Features.Auth.Register.Endpoint>(repo);
        var req = new barakoCMS.Features.Auth.Register.Request
        {
            Username = "existing",
            Email = "test@test.com",
            Password = "password123"
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

    [Fact]
    public async Task Login_Should_Succeed_With_Valid_Credentials()
    {
        // Arrange
        var password = "password123";
        var hash = BCrypt.Net.BCrypt.HashPassword(password);
        var user = new User { Id = Guid.NewGuid(), Username = "testuser", PasswordHash = hash, Role = "User" };

        var repo = Substitute.For<IUserRepository>();
        repo.GetByUsernameAsync("testuser", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<User?>(user));

        // Mock Config
        var config = Substitute.For<IConfiguration>();
        config["JWT:Key"].Returns("super_secret_key_for_testing_purposes_only");

        // Pass config to Factory
        var endpoint = Factory.Create<barakoCMS.Features.Auth.Login.Endpoint>(repo, config);
        
        var req = new barakoCMS.Features.Auth.Login.Request
        {
            Username = "testuser",
            Password = password
        };

        // Act
        await endpoint.HandleAsync(req, CancellationToken.None);

        // Assert
        endpoint.Response.Token.Should().NotBeNullOrEmpty();
    }


}
