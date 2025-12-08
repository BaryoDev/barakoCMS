extern alias App;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Testcontainers.PostgreSql;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using barakoCMS.Core.Interfaces;

namespace AttendancePOC.Tests;

public class CustomWebApplicationFactory : WebApplicationFactory<App::Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgresContainer = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("attendance_test_db")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    public string ConnectionString => _postgresContainer.GetConnectionString().Replace("localhost", "127.0.0.1").Replace("Host=", "Server=") + ";Pooling=false";

    public CustomWebApplicationFactory()
    {
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
                {"SensitivityPolicies:AttendanceRecord:SSN:Action", "Remove"},
                {"SensitivityPolicies:AttendanceRecord:SSN:AllowedRoles:0", "SuperAdmin"},
                {"SensitivityPolicies:AttendanceRecord:BirthDay:Action", "Mask"},
                {"SensitivityPolicies:AttendanceRecord:BirthDay:MaskValue", "***"},
                {"SensitivityPolicies:AttendanceRecord:BirthDay:AllowedRoles:0", "SuperAdmin"},
                {"SensitivityPolicies:AttendanceRecord:BirthDay:AllowedRoles:1", "HR"}
            };
            config.AddInMemoryCollection(settings!);
        });


        builder.ConfigureServices(services =>
        {
            // Replace MockEmailService with our Spy
            services.RemoveAll<IEmailService>();
            services.AddSingleton<IEmailService, SpyEmailService>();
        });
    }

    public class SpyEmailService : IEmailService
    {
        public List<SentEmail> SentEmails { get; } = new();

        public Task SendEmailAsync(string to, string subject, string body, CancellationToken cancellationToken = default)
        {
            SentEmails.Add(new SentEmail(to, subject, body));
            return Task.CompletedTask;
        }
    }

    public record SentEmail(string To, string Subject, string Body);

    public async Task InitializeAsync()
    {
        await _postgresContainer.StartAsync();
    }

    public new async Task DisposeAsync()
    {
        await _postgresContainer.StopAsync();
    }
}
