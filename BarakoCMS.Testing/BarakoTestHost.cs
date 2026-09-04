using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using barakoCMS.Data;
using barakoCMS.Extensions;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using barakoCMS.Modules;
using FastEndpoints;
using Marten;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Testcontainers.PostgreSql;
using Xunit;

namespace BarakoCMS.Testing;

/// <summary>
/// A running barakoCMS for a module's tests: the real host pipeline over a PostgreSQL that
/// Testcontainers starts, with the modules you name registered, the system roles and the initial
/// admin seeded, and every module's seeder run.
/// </summary>
/// <remarks>
/// <para>
/// Use it as an xunit class fixture. Derive a class that names the module and its settings, so
/// xunit can construct it without arguments:
/// </para>
/// <code>
/// public sealed class MyHost : BarakoTestHost
/// {
///     public MyHost() : base(o =>
///     {
///         o.Modules.Add(new MyModule());
///         o.Settings["Modules:MyModule:ApiKey"] = "test";
///     }) { }
/// }
///
/// public class MyModuleTests : IClassFixture&lt;MyHost&gt;
/// </code>
/// <para>
/// <see cref="BarakoTestHost{TModule}"/> covers a module with a parameterless constructor and no
/// settings. One container per fixture, shared by every test in the class; it stops when xunit
/// disposes the fixture.
/// </para>
/// <para>
/// The host is built the way <c>BarakoCMS.Suite</c> builds its own: <c>AddBarakoCMS</c> with
/// discovery off and your modules added, <c>UseBarakoCMS</c>, the schema applied, the core seeder,
/// then the module seeders. Nothing is faked, so a module that fails the contract check or the
/// schema ownership check fails here the way it would fail on a deployment.
/// </para>
/// <para>
/// Each host validates tokens with its own key, so any number of fixtures can share a test
/// process. A host of some other kind in the same process (a <c>WebApplicationFactory</c> over
/// core, say) validates through FastEndpoints' process-wide key, which every host that starts
/// overwrites with its own; give such a host and every <see cref="BarakoTestHost"/> beside it the
/// same <c>Settings["JWT:Key"]</c>.
/// </para>
/// </remarks>
public class BarakoTestHost : IAsyncLifetime
{
    private readonly BarakoTestHostOptions _options;
    private readonly PostgreSqlContainer _postgres;
    private const string JwtIssuer = "BarakoCMS.Testing";
    private const string JwtAudience = "BarakoCMS.Testing";
    private readonly string _jwtKey;
    private WebApplication? _app;

    static BarakoTestHost()
    {
        // Core stores UTC DateTime values and binds them to 'timestamp without time zone', which
        // Npgsql refuses without this switch. Every barakoCMS host sets it before Npgsql loads.
        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
    }

    /// <summary>A host running <paramref name="modules"/> with no extra settings.</summary>
    public BarakoTestHost(params IBarakoModule[] modules)
        : this(o =>
        {
            foreach (var module in modules)
                o.Modules.Add(module);
        })
    {
    }

    /// <summary>A host configured through <see cref="BarakoTestHostOptions"/>.</summary>
    public BarakoTestHost(Action<BarakoTestHostOptions>? configure)
    {
        _options = new BarakoTestHostOptions();
        configure?.Invoke(_options);

        // One key for the host and for the tokens minted here. A JWT:Key set through the options
        // is the one the host validates with, so it has to be the one the clients sign with too.
        _jwtKey = _options.Settings.TryGetValue("JWT:Key", out var configuredKey) && !string.IsNullOrWhiteSpace(configuredKey)
            ? configuredKey
            : Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

        _postgres = new PostgreSqlBuilder()
            .WithImage(_options.PostgresImage)
            .WithDatabase("barako_module_tests")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();
    }

    /// <summary>The host's service provider. Create a scope before resolving anything scoped.</summary>
    public IServiceProvider Services => App.Services;

    /// <summary>The modules this host registered, in registration order.</summary>
    public IReadOnlyList<IBarakoModule> Modules => _options.Modules.ToArray();

    /// <summary>The seeded admin's username.</summary>
    public string AdminUsername => _options.AdminUsername;

    /// <summary>The seeded admin's password, for a test that goes through <c>POST /api/auth/login</c>.</summary>
    public string AdminPassword => _options.AdminPassword;

    /// <summary>
    /// The key this host signs and validates tokens with: <c>Settings["JWT:Key"]</c> when the
    /// options set one, otherwise a fresh random key.
    /// </summary>
    public string JwtKey => _jwtKey;

    /// <summary>The connection string the host is running on, for a test that reads the database directly.</summary>
    public string ConnectionString =>
        _postgres.GetConnectionString().Replace("localhost", "127.0.0.1").Replace("Host=", "Server=") + ";Pooling=false";

    private WebApplication App => _app ?? throw new InvalidOperationException(
        "The host has not started. Use BarakoTestHost as an xunit class fixture (IClassFixture<...>), "
        + "or await InitializeAsync() before using it.");

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();

        // Core reads the process variable in a few places (HTTPS redirection, the schema policy,
        // the demo accounts), so an unset one is filled in to match the host environment. One
        // that is already set is left alone: it belongs to whoever set it.
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")))
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", _options.EnvironmentName);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = _options.EnvironmentName,
        });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(Settings());

        builder.Services.AddBarakoCMS(builder.Configuration, modules =>
        {
            modules.Discover = false;
            foreach (var module in _options.Modules)
                modules.Add(module);
        });

        // Endpoints come from core and from the modules that are running, and from nowhere else.
        // FastEndpoints' own discovery scans every assembly loaded in the process, which in a test
        // process includes whatever some other test touched, and an endpoint from a module nobody
        // registered fails at startup asking for a service nobody added.
        var running = builder.Services
            .Where(d => d.ServiceType == typeof(IBarakoModule))
            .Select(d => d.ImplementationInstance)
            .OfType<IBarakoModule>();
        var endpointAssemblies = new[] { typeof(IBarakoModule).Assembly }
            .Concat(running.SelectMany(m => m.EndpointAssemblies))
            .Distinct()
            .ToArray();
        builder.Services.AddFastEndpoints(o =>
        {
            o.DisableAutoDiscovery = true;
            o.Assemblies = endpointAssemblies;
        });

        // FastEndpoints keeps the JWT signing key in a static (JwtSigningOptions) and validates
        // through a resolver that reads it, so with two hosts in one process the last one to
        // materialise its bearer options validates every token in the process with its own key.
        // Two fixture classes are two hosts, and xunit runs classes in parallel. This host
        // validates with its own key, whatever another host did to the static.
        builder.Services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o =>
        {
            o.TokenValidationParameters.IssuerSigningKeyResolver = null;
            o.TokenValidationParameters.IssuerSigningKey = new SymmetricSecurityKey(Encoding.ASCII.GetBytes(_jwtKey));
        });

        var app = builder.Build();
        app.UseBarakoCMS();

        // The same order as BarakoCMS.Suite: schema before anything reads it, the core seeder for
        // the roles and the admin, then each module's own seeder in its own session.
        await app.ApplyMartenSchemaAsync();
        await DataSeeder.SeedAsync(app);
        await app.RunBarakoModuleSeedersAsync();
        await app.StartAsync();

        _app = app;
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        await _postgres.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    /// <summary>An anonymous client.</summary>
    public HttpClient CreateClient() => App.GetTestClient();

    /// <summary>A client signed in as the seeded admin on the default tenant.</summary>
    public Task<HttpClient> CreateAdminClientAsync(CancellationToken ct = default) =>
        CreateAdminClientAsync(Tenant.DefaultSlug, ct);

    /// <summary>
    /// A client signed in as the seeded admin on <paramref name="tenantSlug"/>, sending the
    /// <c>X-Tenant</c> header the way a tenant-aware caller does.
    /// </summary>
    /// <remarks>
    /// The token issuer refuses a tenant the user is not a member of, so the admin is given an
    /// active membership carrying the same roles it holds globally. Create the tenant first with
    /// <see cref="CreateTenantAsync"/>.
    /// </remarks>
    public async Task<HttpClient> CreateAdminClientAsync(string tenantSlug, CancellationToken ct = default)
    {
        var slug = NormaliseSlug(tenantSlug);
        var admin = await AdminUserAsync(ct);

        if (slug != Tenant.DefaultSlug)
            await EnsureMembershipAsync(admin, slug, ct);

        return await ClientForAsync(admin, slug, ct);
    }

    /// <summary>
    /// A client signed in as a new user holding the named roles, on the default tenant.
    /// </summary>
    /// <remarks>
    /// The roles are the ones the host seeded, so <c>"Admin"</c> carries exactly what an Admin
    /// carries on a deployment, including whatever your module's seeder granted it. A name the
    /// seeder did not create is left out rather than invented, so a caller with an unseeded role
    /// reaches nothing, which is the right answer when a test asks what a stranger can do.
    /// </remarks>
    public async Task<HttpClient> CreateClientAsync(string[] roleNames, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(roleNames);

        var userId = Guid.NewGuid();
        await using (var session = OpenSession())
        {
            var roleIds = new List<Guid>();
            foreach (var name in roleNames)
            {
                var role = await session.Query<Role>().FirstOrDefaultAsync(r => r.Name == name, ct);
                if (role is not null)
                    roleIds.Add(role.Id);
            }

            session.Store(new User
            {
                Id = userId,
                Username = $"test-{userId:N}"[..20],
                Email = $"test-{userId:N}@example.com",
                RoleIds = roleIds,
            });
            await session.SaveChangesAsync(ct);
        }

        var user = await LoadUserAsync(userId, ct);
        return await ClientForAsync(user, Tenant.DefaultSlug, ct);
    }

    /// <summary>The same, for a single role.</summary>
    public Task<HttpClient> CreateClientAsync(string roleName, CancellationToken ct = default) =>
        CreateClientAsync([roleName], ct);

    /// <summary>
    /// Creates an active tenant and returns its slug. A null slug gets a random one, so two tests
    /// in one class cannot share a tenant by accident.
    /// </summary>
    public async Task<string> CreateTenantAsync(string? slug = null, CancellationToken ct = default)
    {
        var value = NormaliseSlug(slug ?? $"t-{Guid.NewGuid():N}"[..14]);

        await using var session = OpenSession();
        var existing = await session.Query<Tenant>().FirstOrDefaultAsync(t => t.Slug == value, ct);
        if (existing is null)
        {
            session.Store(new Tenant { Id = Guid.NewGuid(), Slug = value, Name = value, IsActive = true });
            await session.SaveChangesAsync(ct);
        }

        return value;
    }

    /// <summary>
    /// A Marten session on the store, for arranging data or reading what an endpoint wrote. The
    /// default tenant when <paramref name="tenantSlug"/> is null, otherwise that tenant's partition,
    /// the same way the host's own sessions are opened.
    /// </summary>
    public IDocumentSession OpenSession(string? tenantSlug = null)
    {
        var store = Services.GetRequiredService<IDocumentStore>();
        var slug = tenantSlug is null ? Tenant.DefaultSlug : NormaliseSlug(tenantSlug);
        return slug == Tenant.DefaultSlug ? store.LightweightSession() : store.LightweightSession(slug);
    }

    private IEnumerable<KeyValuePair<string, string?>> Settings()
    {
        var settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["ConnectionStrings:DefaultConnection"] = ConnectionString,
            // Outranks ConnectionStrings when set, and another fixture in the same process may have
            // set it in the environment. Blank here so this host reaches its own container.
            ["DATABASE_URL"] = string.Empty,
            ["JWT:Key"] = _jwtKey,
            ["JWT:Issuer"] = JwtIssuer,
            ["JWT:Audience"] = JwtAudience,
            ["InitialAdmin:Username"] = _options.AdminUsername,
            ["InitialAdmin:Password"] = _options.AdminPassword,
            // The OpenAPI document is something a module test may assert on, whatever the
            // environment, and the demo attendance content is not something any module needs.
            ["Swagger:Enabled"] = "true",
            ["Seed:DemoContent"] = "false",
        };

        foreach (var (key, value) in _options.Settings)
            settings[key] = value;

        return settings;
    }

    /// <summary>
    /// Mints the token the host's own issuer would mint, with the same claims: the roles, the
    /// user id the capability gate reads the stored roles through, the tenant the access
    /// middleware compares against <c>X-Tenant</c>, and the issued-at the session epoch check needs.
    /// </summary>
    /// <remarks>
    /// Signed here rather than through <c>ITokenIssuer</c>, which reaches FastEndpoints'
    /// process-wide service resolver. That resolver belongs to whichever host started last, so
    /// with two hosts in one test process, which is what two fixture classes are, it can point at
    /// a disposed provider. A helper that fails depending on which class ran first is not a helper.
    /// </remarks>
    private async Task<HttpClient> ClientForAsync(User user, string tenantSlug, CancellationToken ct)
    {
        IReadOnlyList<string> roles;
        await using (var session = OpenSession())
        {
            roles = await session.Query<Role>()
                .Where(r => user.RoleIds.Contains(r.Id))
                .Select(r => r.Name)
                .ToListAsync(ct);
        }

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new(JwtRegisteredClaimNames.Iat,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)),
            new("UserId", user.Id.ToString()),
            new("Username", user.Username),
            new("tenant", tenantSlug),
        };
        foreach (var role in roles)
            claims.Add(new Claim(ClaimTypes.Role, role));
        if (roles.Count == 0)
            claims.Add(new Claim(ClaimTypes.Role, "User"));

        // ASCII, the encoding the host's own issuer uses for the same key.
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.ASCII.GetBytes(_jwtKey)), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            issuer: JwtIssuer,
            audience: JwtAudience,
            claims: claims,
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: credentials));

        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (tenantSlug != Tenant.DefaultSlug)
            client.DefaultRequestHeaders.Add(TenantResolutionMiddleware.TenantHeader, tenantSlug);

        return client;
    }

    private async Task<User> AdminUserAsync(CancellationToken ct)
    {
        await using var session = OpenSession();
        var admin = await session.Query<User>().FirstOrDefaultAsync(u => u.Username == _options.AdminUsername, ct);
        return admin ?? throw new InvalidOperationException(
            $"The seeded admin '{_options.AdminUsername}' is not in the database. The core seeder did not run, or a test removed it.");
    }

    private async Task<User> LoadUserAsync(Guid id, CancellationToken ct)
    {
        await using var session = OpenSession();
        return await session.LoadAsync<User>(id, ct)
            ?? throw new InvalidOperationException($"User {id} was stored and could not be read back.");
    }

    private async Task EnsureMembershipAsync(User user, string tenantSlug, CancellationToken ct)
    {
        await using var session = OpenSession();

        var tenant = await session.Query<Tenant>().FirstOrDefaultAsync(t => t.Slug == tenantSlug, ct);
        if (tenant is null)
        {
            throw new InvalidOperationException(
                $"Tenant '{tenantSlug}' does not exist. Create it with CreateTenantAsync first.");
        }

        var member = await session.Query<Membership>()
            .AnyAsync(m => m.UserId == user.Id && m.TenantSlug == tenantSlug, ct);
        if (member)
            return;

        session.Store(new Membership
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TenantSlug = tenantSlug,
            Status = MembershipStatus.Active,
            RoleIds = [.. user.RoleIds],
        });
        await session.SaveChangesAsync(ct);
    }

    private static string NormaliseSlug(string slug)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        return slug.Trim().ToLowerInvariant();
    }
}

/// <summary>
/// A host running one module that has a parameterless constructor and needs no settings:
/// <c>IClassFixture&lt;BarakoTestHost&lt;MyModule&gt;&gt;</c>.
/// </summary>
public sealed class BarakoTestHost<TModule> : BarakoTestHost
    where TModule : IBarakoModule, new()
{
    public BarakoTestHost() : base(new TModule())
    {
    }
}
