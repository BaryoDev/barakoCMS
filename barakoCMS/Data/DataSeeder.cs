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
                    Role = "Admin",
                    CreatedAt = DateTime.UtcNow
                };

                session.Store(adminUser);
                await session.SaveChangesAsync();
                
                Console.WriteLine($"[DataSeeder] Initial Admin created: {username}");
            }
        }
    }
}
