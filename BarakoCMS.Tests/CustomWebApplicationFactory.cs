using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Marten;
using JasperFx.Events.Projections;

namespace BarakoCMS.Tests;

public class CustomWebApplicationFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgresContainer = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("barako_test_db")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    public string ConnectionString => _postgresContainer.GetConnectionString().Replace("localhost", "127.0.0.1").Replace("Host=", "Server=") + ";Pooling=false";

    public CustomWebApplicationFactory()
    {
        // Set environment variables before the host starts
        Environment.SetEnvironmentVariable("JWT__Key", "test-super-secret-key-that-is-at-least-32-chars-long");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((ctx, config) =>
        {
            var settings = new Dictionary<string, string>
            {
                {"ConnectionStrings:DefaultConnection", ConnectionString},
                {"JWT:Key", "test-super-secret-key-that-is-at-least-32-chars-long"},
                {"JWT:Issuer", "BarakoTest"},
                {"JWT:Audience", "BarakoClient"},
                {"BarakoCMS:StrictValidation", "true"}, // Enable strict validation for tests
                {"BarakoCMS:ValidationOptions:EnforceFieldTypes", "true"},
                {"BarakoCMS:ValidationOptions:EnforcePascalCaseFieldNames", "true"},
                {"BarakoCMS:ValidationOptions:ValidateDataTypes", "true"}
            };
            config.AddInMemoryCollection(settings!);
        });

        // Override Marten configuration for tests to use INLINE projections
        builder.ConfigureServices(services =>
        {
            // Remove the existing Marten configuration
            var martenDescriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IDocumentStore));
            if (martenDescriptor != null)
            {
                services.Remove(martenDescriptor);
            }

            // Re-add Marten with INLINE projections for immediate consistency in tests
            services.AddMarten(sp =>
            {
                var options = new StoreOptions();
                options.Connection(ConnectionString);

                // Configure document versioning
                options.Schema.For<barakoCMS.Models.Content>().DocumentAlias("contents");
                options.Schema.For<barakoCMS.Models.User>().DocumentAlias("users");
                options.Schema.For<barakoCMS.Models.SystemSetting>().DocumentAlias("system_settings");

                // Register Workflow Projection as INLINE for tests (not Async)
                options.Projections.Add(new barakoCMS.Features.Workflows.WorkflowProjection(sp), ProjectionLifecycle.Inline);

                return options;
            })
            .UseLightweightSessions();
            // Note: No AddAsyncDaemon for tests - projections run synchronously
        });
    }

    public async ValueTask InitializeAsync()
    {
        await _postgresContainer.StartAsync();

        // Same order-proof canonical role seeding as IntegrationTestFixture; see the
        // comment there. xunit v3 execution order made role creation racy by name.
        using var scope = Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession();
        foreach (var role in new[]
                 {
                     new barakoCMS.Models.Role { Id = barakoCMS.Data.DataSeeder.SuperAdminRoleId, Name = "SuperAdmin", Description = "Full system access" },
                     new barakoCMS.Models.Role { Id = barakoCMS.Data.DataSeeder.AdminRoleId, Name = "Admin", Description = "Administrator with full access" },
                     new barakoCMS.Models.Role { Id = barakoCMS.Data.DataSeeder.HRRoleId, Name = "HR", Description = "Human Resources - manage attendance" },
                     new barakoCMS.Models.Role { Id = barakoCMS.Data.DataSeeder.UserRoleId, Name = "User", Description = "Standard user" },
                 })
        {
            if (await session.Query<barakoCMS.Models.Role>().FirstOrDefaultAsync(r => r.Name == role.Name) is null)
                session.Store(role);
        }
        await session.SaveChangesAsync();
    }

    public new async ValueTask DisposeAsync()
    {
        await _postgresContainer.StopAsync();
    }
}
