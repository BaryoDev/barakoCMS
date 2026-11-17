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
    [Fact]
    /// <summary>
    /// Registering a username and address that already exist creates nothing, and says nothing.
    /// </summary>
    /// <remarks>
    /// This used to assert the opposite half: that the endpoint failed validation with "Username or
    /// Email already exists". That message was an enumeration oracle on an anonymous endpoint (#268),
    /// so what is worth pinning is that the duplicate is not stored while the caller is told nothing.
    /// Driven with verification off, because that is the branch that reads the repository; the
    /// verified path queries Marten directly and is covered end to end in EmailVerificationTests.
    /// </remarks>
    public async Task Register_Should_Not_Disclose_That_The_User_Exists()
    {
        // Arrange
        var repo = Substitute.For<IUserRepository>();
        var session = Substitute.For<IDocumentSession>();
        var passwordValidator = new barakoCMS.Infrastructure.Services.PasswordPolicyValidator();

        repo.GetByUsernameOrEmailAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new User { Username = "existinguser" });

        var endpoint = Factory.Create<barakoCMS.Features.Auth.Register.Endpoint>(
            repo,
            session,
            new barakoCMS.Infrastructure.Multitenancy.TenantContext(),
            passwordValidator,
            Substitute.For<barakoCMS.Core.Interfaces.IEmailVerificationService>(),
            new barakoCMS.Infrastructure.Auth.EmailVerificationOptions
            {
                Required = false,
                AcknowledgeUnverifiedRegistration = true,
            });
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
        catch (ValidationFailureException) { throw; }
        catch { /* Send.ResponseAsync has no HttpContext under Factory.Create */ }

        // Assert
        endpoint.ValidationFailed.Should().BeFalse("refusing here tells an anonymous caller the account exists");
        repo.DidNotReceive().Store(Arg.Any<User>());
    }
}
