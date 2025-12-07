using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace BarakoCMS.Tests;

public class LocalDbWebApplicationFactory : WebApplicationFactory<Program>
{
    private const string LocalConnectionString = "Host=localhost;Port=5432;Database=barako_test_db;Username=postgres;Password=postgres";

    public LocalDbWebApplicationFactory()
    {
        Environment.SetEnvironmentVariable("JWT__Key", "test-super-secret-key-that-is-at-least-32-chars-long");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((ctx, config) =>
        {
            var settings = new Dictionary<string, string>
            {
                {"ConnectionStrings:DefaultConnection", LocalConnectionString},
                {"BarakoCMS:StrictValidation", "true"},
                {"BarakoCMS:ValidationOptions:EnforceFieldTypes", "true"},
                {"BarakoCMS:ValidationOptions:EnforcePascalCaseFieldNames", "true"},
                {"BarakoCMS:ValidationOptions:ValidateDataTypes", "true"}
            };
            config.AddInMemoryCollection(settings!);
        });
    }
}
