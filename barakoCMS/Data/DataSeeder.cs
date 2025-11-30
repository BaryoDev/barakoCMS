using Marten;
using barakoCMS.Models;

namespace barakoCMS.Data;

public static class DataSeeder
{
    public static async Task SeedAsync(IHost host)
    {
        using var scope = host.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();

        // Ensure Roles exist
        var adminRole = await session.Query<Role>().FirstOrDefaultAsync(r => r.Name == "Admin");
        if (adminRole == null)
        {
            adminRole = new Role { Id = Guid.NewGuid(), Name = "Admin", Description = "Administrator with full access" };
            session.Store(adminRole);
        }

        var userRole = await session.Query<Role>().FirstOrDefaultAsync(r => r.Name == "User");
        if (userRole == null)
        {
            userRole = new Role { Id = Guid.NewGuid(), Name = "User", Description = "Standard user" };
            session.Store(userRole);
        }

        // Check if any users exist
        var userCount = await session.Query<User>().CountAsync();
        if (userCount == 0)
        {
            var adminConfig = configuration.GetSection("InitialAdmin");
            var username = adminConfig["Username"];
            var password = adminConfig["Password"];

            if (!string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password))
            {
                var adminUser = new User
                {
                    Id = Guid.NewGuid(),
                    Username = username,
                    Email = $"{username}@localhost", // Default email
                    PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
                    RoleIds = new List<Guid> { adminRole.Id },
                    CreatedAt = DateTime.UtcNow
                };

                session.Store(adminUser);
                await session.SaveChangesAsync();
                
                Console.WriteLine($"[DataSeeder] Initial Admin created: {username}");
            }
        }
        else
        {
            // Ensure changes to roles are saved if users already existed
            await session.SaveChangesAsync();
        }
    }
}
