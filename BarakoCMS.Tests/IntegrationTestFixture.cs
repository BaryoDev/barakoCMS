using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;
using Xunit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Marten;
using barakoCMS.Extensions;
using Microsoft.Extensions.Configuration;

namespace BarakoCMS.Tests;

public class IntegrationTestFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgresContainer = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("barako_test_db")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .WithPassword("postgres")
        .Build();

    public IntegrationTestFixture()
    {
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");
        Environment.SetEnvironmentVariable("JWT__Key", "test-super-secret-key-that-is-at-least-32-chars-long");
    }

    public string ConnectionString => _postgresContainer.GetConnectionString().Replace("localhost", "127.0.0.1").Replace("Host=", "Server=") + ";Pooling=false";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((ctx, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "ConnectionStrings:DefaultConnection", ConnectionString },
                { "JWT:Key", "test-super-secret-key-that-is-at-least-32-chars-long" },
                { "JWT:Issuer", "BarakoTest" },
                { "JWT:Audience", "BarakoClient" }
            });
        });
    }

    public async Task InitializeAsync()
    {
        await _postgresContainer.StartAsync();
        // Explicitly set DATABASE_URL to ensure ResolveConnectionString picks it up.
        // Even if Uri parsing fails, it falls back to the raw string, which is what we want.
        Environment.SetEnvironmentVariable("DATABASE_URL", ConnectionString);
    }

    public new async Task DisposeAsync()
    {
        await _postgresContainer.DisposeAsync();
        await base.DisposeAsync();
    }
}
