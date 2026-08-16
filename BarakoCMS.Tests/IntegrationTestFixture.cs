using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;
using Xunit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
                { "JWT:Audience", "BarakoClient" },
                { "Feeds:Paths:sitemap_paths", "/articles/{slug}" }
            });
        });

        // The test project references the Files module, so FastEndpoints discovers its endpoints.
        // Register the module's services + schema so those endpoints can activate (Postgres storage;
        // S3 stays dormant with no Files:S3 config). This also gives us a host that can test the
        // Files endpoints end to end.
        builder.ConfigureServices((ctx, services) =>
        {
            new BarakoCMS.Files.FilesModule().ConfigureServices(services, ctx.Configuration);
            services.ConfigureMarten(opts => new BarakoCMS.Files.FilesModule().ConfigureMarten(opts));

            // AI module: register its schema/endpoints, but swap the Ollama client for a deterministic
            // fake so the search tests run without an embedding backend.
            new BarakoCMS.AI.AiModule().ConfigureServices(services, ctx.Configuration);
            services.ConfigureMarten(opts => new BarakoCMS.AI.AiModule().ConfigureMarten(opts));
            services.RemoveAll<BarakoCMS.AI.IEmbeddingClient>();
            services.AddSingleton<BarakoCMS.AI.IEmbeddingClient, FakeEmbeddingClient>();

            // Accounting: registers its content lifecycle hooks (the balance invariant and the
            // chart-of-accounts rules), so the generic /api/contents endpoint can be tested with
            // real domain rules attached — the whole point of modelling accounting as content.
            new BarakoCMS.Accounting.AccountingModule().ConfigureServices(services, ctx.Configuration);
            services.ConfigureMarten(opts => new BarakoCMS.Accounting.AccountingModule().ConfigureMarten(opts));

            // Diagnostics: the test project references it, so FastEndpoints already discovers
            // /api/client-errors — register its schema too so those endpoints can actually run.
            services.ConfigureMarten(opts => new BarakoCMS.Diagnostics.DiagnosticsModule().ConfigureMarten(opts));

            // Pwa: same reason — the assembly is referenced, so /api/pwa/* is discovered; register the
            // schema so those endpoints can run.
            services.ConfigureMarten(opts => new BarakoCMS.Pwa.PwaModule().ConfigureMarten(opts));
        });
    }

    public async Task InitializeAsync()
    {
        await _postgresContainer.StartAsync();
        // Explicitly set Env Vars to ensure they are available for Program.cs builder
        Environment.SetEnvironmentVariable("DATABASE_URL", ConnectionString);
        Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection", ConnectionString);
        Environment.SetEnvironmentVariable("SKIP_SEEDER", "true");
    }

    public new async Task DisposeAsync()
    {
        await _postgresContainer.DisposeAsync();
        await base.DisposeAsync();
    }

    /// <summary>
    /// Creates a JWT token using standard libraries, avoiding FastEndpoints' static ServiceResolver issue.
    /// </summary>
    public string CreateToken(string[] roles, string? userId = null, Dictionary<string, string>? additionalClaims = null)
    {
        var signingKey = "test-super-secret-key-that-is-at-least-32-chars-long";
        var issuer = "BarakoTest";
        var audience = "BarakoClient";

        var securityKey = new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(
            System.Text.Encoding.UTF8.GetBytes(signingKey));
        var credentials = new Microsoft.IdentityModel.Tokens.SigningCredentials(
            securityKey, Microsoft.IdentityModel.Tokens.SecurityAlgorithms.HmacSha256);

        var claims = new List<System.Security.Claims.Claim>();

        foreach (var role in roles)
        {
            claims.Add(new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, role));
        }

        claims.Add(new System.Security.Claims.Claim("UserId", userId ?? Guid.NewGuid().ToString()));

        if (additionalClaims != null)
        {
            foreach (var kvp in additionalClaims)
            {
                claims.Add(new System.Security.Claims.Claim(kvp.Key, kvp.Value));
            }
        }

        var token = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            expires: DateTime.UtcNow.AddDays(1),
            signingCredentials: credentials);

        return new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().WriteToken(token);
    }
}
