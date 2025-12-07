using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Testcontainers.PostgreSql;

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
                {"BarakoCMS:StrictValidation", "true"}, // Enable strict validation for tests
                {"BarakoCMS:ValidationOptions:EnforceFieldTypes", "true"},
                {"BarakoCMS:ValidationOptions:EnforcePascalCaseFieldNames", "true"},
                {"BarakoCMS:ValidationOptions:ValidateDataTypes", "true"}
            };
            config.AddInMemoryCollection(settings!);
        });
    }

    public async Task InitializeAsync()
    {
        await _postgresContainer.StartAsync();
    }

    public new async Task DisposeAsync()
    {
        await _postgresContainer.StopAsync();
    }
}
