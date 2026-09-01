using FastEndpoints;
using static BarakoCMS.Tests.ModuleSchemaTestHelper;
using barakoCMS.Modules;
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
                // The OpenAPI document is a shipped artifact (tags group a generated client, and the
                // delivery paths are generated per tenant), so tests assert against it. Explicit
                // rather than relying on ASPNETCORE_ENVIRONMENT, which is process-global and set by
                // whichever factory happened to construct last.
                { "Swagger:Enabled", "true" },
                { "Feeds:SiteUrl", "https://test.example.com" },
                { "Feeds:Paths:sitemap_paths", "/articles/{slug}" },
            });
        });

        // The test project references the Files module, so FastEndpoints discovers its endpoints.
        // Register the module's services + schema so those endpoints can activate (Postgres storage;
        // S3 stays dormant with no Files:S3 config). This also gives us a host that can test the
        // Files endpoints end to end.
        builder.ConfigureServices((ctx, services) =>
        {
            // Let a test choose its own client IP so the auth rate limiter (5 attempts /
            // 15 min per IP) partitions per test instead of sharing one loopback bucket.
            // Without this, xunit v3 ordering packs enough auth calls into one window that a
            // later login gets 429 and a test asserting success fails. See TestRemoteIpFilter.
            services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter, TestRemoteIpFilter>();

            new BarakoCMS.Email.Resend.ResendEmailModule().ConfigureServices(services, ctx.Configuration);
            services.ConfigureMarten(opts => ConfigureVia(new BarakoCMS.Email.Resend.ResendEmailModule(), opts));

            new BarakoCMS.Files.FilesModule().ConfigureServices(services, ctx.Configuration);
            services.ConfigureMarten(opts => ConfigureVia(new BarakoCMS.Files.FilesModule(), opts));

            // AI module: register its schema/endpoints, but swap the Ollama client for a deterministic
            // fake so the search tests run without an embedding backend.
            new BarakoCMS.AI.AiModule().ConfigureServices(services, ctx.Configuration);
            services.ConfigureMarten(opts => ConfigureVia(new BarakoCMS.AI.AiModule(), opts));
            services.RemoveAll<BarakoCMS.AI.IEmbeddingClient>();
            services.AddSingleton<BarakoCMS.AI.IEmbeddingClient, FakeEmbeddingClient>();

            // Accounting: registers its content lifecycle hooks (the balance invariant and the
            // chart-of-accounts rules), so the generic /api/contents endpoint can be tested with
            // real domain rules attached — the whole point of modelling accounting as content.
            new BarakoCMS.Accounting.AccountingModule().ConfigureServices(services, ctx.Configuration);
            services.ConfigureMarten(opts => ConfigureVia(new BarakoCMS.Accounting.AccountingModule(), opts));

            // Diagnostics: the test project references it, so FastEndpoints already discovers
            // /api/client-errors — register its schema too so those endpoints can actually run.
            services.ConfigureMarten(opts => ConfigureVia(new BarakoCMS.Diagnostics.DiagnosticsModule(), opts));

            // Pwa: same reason — the assembly is referenced, so /api/pwa/* is discovered; register the
            // schema so those endpoints can run.
            services.ConfigureMarten(opts => ConfigureVia(new BarakoCMS.Pwa.PwaModule(), opts));

            // FeatureFlags: /api/feature-flags is anonymous, so which keys it hands out is a test
            // this project has to be able to run.
            new BarakoCMS.FeatureFlags.FeatureFlagsModule().ConfigureServices(services, ctx.Configuration);
            services.ConfigureMarten(opts => ConfigureVia(new BarakoCMS.FeatureFlags.FeatureFlagsModule(), opts));

            // ExternalAuth: the OAuth start/callback endpoints are anonymous, so what they refuse
            // is only checkable over real HTTP. ConfigureServices gives them IHttpClientFactory.
            new BarakoCMS.ExternalAuth.ExternalAuthModule().ConfigureServices(services, ctx.Configuration);
            services.ConfigureMarten(opts => ConfigureVia(new BarakoCMS.ExternalAuth.ExternalAuthModule(), opts));

            // DeviceTrust: replaces core's no-op gate and contributes a global pre-processor.
            // DeviceTrust:Enforce is unset here, so the processor returns before doing anything and
            // the gate behaves like the no-op it replaces. The enforcement tests turn it on with
            // WithSettings, which builds a separate host.
            new BarakoCMS.DeviceTrust.DeviceTrustModule().ConfigureServices(services, ctx.Configuration);
            services.ConfigureMarten(opts => ConfigureVia(new BarakoCMS.DeviceTrust.DeviceTrustModule(), opts));

            // Analytics.Umami: registered with its own section, exactly as the host scopes it, and
            // with the outbound handler stubbed. A test that let this reach the network would be
            // asserting about the internet.
            new BarakoCMS.Analytics.Umami.UmamiAnalyticsModule()
                .ConfigureServices(services, ctx.Configuration.GetSection(
                    BarakoCMS.Analytics.Umami.UmamiOptions.SectionName));
            services.AddHttpClient<BarakoCMS.Analytics.Umami.IUmamiClient, BarakoCMS.Analytics.Umami.UmamiClient>()
                .ConfigurePrimaryHttpMessageHandler(() => new UmamiStubHandler());

            // FastEndpoints 8 discovers endpoints eagerly inside AddFastEndpoints, which ran in
            // Program.cs before any module assembly above was loaded, so none of the module
            // endpoints exist in that scan. Re-register with the module assemblies explicit;
            // FE registers EndpointData with a plain AddSingleton, so this last one wins.
            services.AddFastEndpoints(o => o.Assemblies = ModuleEndpointAssemblies);
        });
    }

    /// <summary>
    /// Every module assembly whose endpoints this host serves. Named rather than inlined so a test
    /// that has to rebuild the host cannot quietly serve a smaller API than the fixture does.
    /// </summary>
    public static readonly System.Reflection.Assembly[] ModuleEndpointAssemblies =
    [
        typeof(BarakoCMS.Email.Resend.ResendEmailModule).Assembly,
        typeof(BarakoCMS.Files.FilesModule).Assembly,
        typeof(BarakoCMS.AI.AiModule).Assembly,
        typeof(BarakoCMS.Accounting.AccountingModule).Assembly,
        typeof(BarakoCMS.Diagnostics.DiagnosticsModule).Assembly,
        typeof(BarakoCMS.Pwa.PwaModule).Assembly,
        typeof(BarakoCMS.FeatureFlags.FeatureFlagsModule).Assembly,
        // Portability owns no documents of its own and registers no services, so its
        // endpoints only need discovering.
        typeof(BarakoCMS.Portability.PortabilityModule).Assembly,
        typeof(BarakoCMS.ExternalAuth.ExternalAuthModule).Assembly,
        typeof(BarakoCMS.DeviceTrust.DeviceTrustModule).Assembly,
        typeof(BarakoCMS.Import.ImportModule).Assembly,
        typeof(BarakoCMS.Analytics.Umami.UmamiAnalyticsModule).Assembly,
    ];

    public async ValueTask InitializeAsync()
    {
        await _postgresContainer.StartAsync();
        // Explicitly set Env Vars to ensure they are available for Program.cs builder
        Environment.SetEnvironmentVariable("DATABASE_URL", ConnectionString);
        Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection", ConnectionString);
        Environment.SetEnvironmentVariable("SKIP_SEEDER", "true");

        // Canonical system roles with their well-known ids, before any test runs.
        // xunit v3 changed execution order inside the Sequential collection, and
        // several tests create a role named SuperAdmin with a random id when none
        // exists by name; the fixed-id insert that used to run first then dies on
        // the unique name index. Seeding here makes role state order-proof.
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
        await _postgresContainer.DisposeAsync();
        await base.DisposeAsync();
    }

    /// <summary>
    /// Creates a JWT token using standard libraries, avoiding FastEndpoints' static ServiceResolver issue.
    /// </summary>
    /// <summary>
    /// A host with one configuration value overridden, for tests about configuration-dependent
    /// behaviour. Null removes the key, which is the case worth testing: a setting nobody set.
    ///
    /// Do NOT dispose the result. A derived factory shares this fixture's server, so disposing it
    /// tears down the host for every test that runs afterwards. The fixture owns the lifetime.
    /// </summary>
    public WebApplicationFactory<Program> WithSetting(string key, string? value) =>
        WithSettings(new Dictionary<string, string?> { { key, value } });

    /// <summary>
    /// The same, for behaviour that only appears once several settings agree. Turning a module on
    /// usually takes more than one key, and setting them one host at a time gets you a host that
    /// has the flag but not the URL.
    ///
    /// Do NOT dispose the result, for the reason given above.
    /// </summary>
    public WebApplicationFactory<Program> WithSettings(IDictionary<string, string?> settings) =>
        WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(settings)));

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

        // Matches what TokenIssuer mints. Without it the session epoch check has nothing to compare
        // against and serves every request, so a test suite using this helper would report the
        // control as working while it did nothing.
        claims.Add(new System.Security.Claims.Claim(
            System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Iat,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)));

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
