using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace BarakoCMS.Tests;

public static class TestHelpers
{
    /// <summary>
    /// Creates an admin user with SuperAdmin role for testing purposes.
    /// </summary>
    public static async Task<(string token, Guid userId)> CreateAdminUserAsync(
        IntegrationTestFixture fixture)
    {
        using var scope = fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        // Create SuperAdmin role if it doesn't exist
        var superAdminRole = await session.Query<barakoCMS.Models.Role>()
            .FirstOrDefaultAsync(r => r.Name == "SuperAdmin");

        if (superAdminRole == null)
        {
            superAdminRole = new barakoCMS.Models.Role
            {
                Id = Guid.NewGuid(),
                Name = "SuperAdmin",
                Permissions = new List<barakoCMS.Models.ContentTypePermission>()
            };
            session.Store(superAdminRole);
        }

        // Create user with SuperAdmin role
        var userId = Guid.NewGuid();
        var user = new barakoCMS.Models.User
        {
            Id = userId,
            Username = $"admin-{userId}",
            Email = $"admin-{userId}@test.com",
            RoleIds = new List<Guid> { superAdminRole.Id }
        };
        session.Store(user);
        await session.SaveChangesAsync();

        var token = fixture.CreateToken(roles: new[] { "Admin", "SuperAdmin" }, userId: userId.ToString());
        return (token, userId);
    }
}
