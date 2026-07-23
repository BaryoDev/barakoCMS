using FastEndpoints;
using FastEndpoints.Swagger;
using FastEndpoints.Security;
using Marten;
using Marten.Events.Projections;
using Marten.Events.Daemon;
using Weasel.Core;
using barakoCMS.Features.Workflows;
using barakoCMS.Models;
using barakoCMS.Modules;
using barakoCMS.Repository;
using System.Threading.RateLimiting;
using barakoCMS.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using JasperFx.Events;
using JasperFx.Core;

namespace barakoCMS.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddBarakoCMS(this IServiceCollection services, IConfiguration configuration)
        => services.AddBarakoCMS(configuration, configureModules: null);

    /// <summary>
    /// Registers barakoCMS core plus any optional feature modules. Modules are purely additive:
    /// with no modules this behaves exactly like the core-only overload. Each module can contribute
    /// services, Marten document types, endpoints (from its own assembly), and seed data.
    /// </summary>
    public static IServiceCollection AddBarakoCMS(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<BarakoModuleBuilder>? configureModules)
    {
        // Collect opted-in modules first, so their endpoint assemblies and Marten config are
        // available when we wire up FastEndpoints and Marten below.
        var moduleBuilder = new BarakoModuleBuilder();
        configureModules?.Invoke(moduleBuilder);
        var modules = moduleBuilder.Modules;

        foreach (var module in modules)
        {
            // Keep the instance discoverable at runtime (used by the seed runner).
            services.AddSingleton<IBarakoModule>(module);
            module.ConfigureServices(services, configuration);
        }

        // FastEndpoints scans the entry (host) assembly by default; add each module's assembly so
        // endpoints shipped inside a module DLL are discovered too. DisableAutoDiscovery stays false,
        // so this augments rather than replaces the host scan.
        var moduleAssemblies = modules
            .SelectMany(m => m.EndpointAssemblies)
            .Distinct()
            .ToArray();
        if (moduleAssemblies.Length > 0)
            services.AddFastEndpoints(o => o.Assemblies = moduleAssemblies);
        else
            services.AddFastEndpoints();

        // Request body size limit (defends against large-payload memory pressure / DoS on the
        // arbitrary-JSON content endpoints). Configurable via RequestLimits:MaxBodyBytes; default 10 MB.
        var maxBodyBytes = configuration.GetValue<long?>("RequestLimits:MaxBodyBytes") ?? 10L * 1024 * 1024;
        services.Configure<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>(o =>
        {
            o.Limits.MaxRequestBodySize = maxBodyBytes;
        });
        services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o =>
        {
            o.MultipartBodyLengthLimit = maxBodyBytes;
        });
        if (Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") == "Development")
        {
            services.SwaggerDocument();
        }

        var connectionString = ResolveConnectionString(configuration);

        // Thresholds are configurable. The memory default is deliberately generous:
        // .NET's server GC holds ~1.3GB of private memory on an idle container, so a
        // 1GB ceiling reports Unhealthy on a perfectly healthy boot.
        var maxMemoryMb = configuration.GetValue<long?>("HealthChecks:MaxPrivateMemoryMegabytes") ?? 4096;
        var minFreeDiskMb = configuration.GetValue<long?>("HealthChecks:MinimumFreeDiskMegabytes") ?? 512;

        services.AddHealthChecks()
            .AddNpgSql(connectionString, name: "Database", tags: new[] { "db", "ready" })
            .AddDiskStorageHealthCheck(setup =>
            {
                setup.AddDrive(@"/", minimumFreeMegabytes: minFreeDiskMb);
                setup.CheckAllDrives = false;
            }, name: "Disk Space")
            .AddPrivateMemoryHealthCheck(maxMemoryMb * 1024 * 1024, name: "Memory");

        // Validate JWT key exists and has minimum length for security. Fail fast rather than
        // booting with broken or insecure auth. Check both config and the JWT__Key env var.
        var jwtKey = configuration["JWT:Key"];
        if (string.IsNullOrWhiteSpace(jwtKey))
        {
            jwtKey = Environment.GetEnvironmentVariable("JWT__Key");
        }
        if (string.IsNullOrWhiteSpace(jwtKey) || jwtKey.Length < 32)
        {
            throw new InvalidOperationException("JWT:Key must be configured and at least 32 characters (256 bits) for security.");
        }

        services.AddJWTBearerAuth(jwtKey, tokenValidation: p =>
        {
            p.ValidateIssuerSigningKey = true;
            p.ValidateIssuer = true;
            p.ValidateAudience = true;
            p.ValidIssuer = configuration["JWT:Issuer"];
            p.ValidAudience = configuration["JWT:Audience"];

            // Explicitly map claims
            p.NameClaimType = "Username";
            p.RoleClaimType = System.Security.Claims.ClaimTypes.Role;

            // Strict token expiration - no clock skew tolerance
            p.ClockSkew = TimeSpan.Zero;
        });
        services.AddAuthorization();
        services.AddCors(options =>
        {
            options.AddPolicy("SecurePolicy", builder =>
            {
                // Get allowed origins from configuration (comma-separated list)
                // Priority: CORS__AllowedOrigins env var > appsettings.json CORS:AllowedOrigins
                var allowedOrigins = configuration["CORS:AllowedOrigins"]?
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    ?? Array.Empty<string>();

                if (allowedOrigins.Length > 0)
                {
                    builder.WithOrigins(allowedOrigins)
                           .AllowAnyMethod()
                           .AllowAnyHeader()
                           .AllowCredentials();
                }
                else
                {
                    // Fallback to localhost for development if no origins configured
                    builder.WithOrigins("http://localhost:3000", "http://localhost:3001", "https://localhost:7049")
                           .AllowAnyMethod()
                           .AllowAnyHeader()
                           .AllowCredentials();
                }
            });
        });
        // Repository registration
        services.AddScoped<IUserRepository, MartenUserRepository>();

        // RBAC Services
        services.AddScoped<IConditionEvaluator, ConditionEvaluator>();
        
        // Permission Resolver with Caching
        services.AddScoped<PermissionResolver>(); // Inner resolver
        services.AddScoped<IPermissionResolver, CachedPermissionResolver>(); // Cached decorator
        
        // Security Services
        // The only place an access token is minted — it owns the "may this user hold a token for
        // this tenant?" check, so no endpoint can skip it by omission. See ITokenIssuer.
        services.AddScoped<barakoCMS.Infrastructure.Auth.ITokenIssuer, barakoCMS.Infrastructure.Auth.TokenIssuer>();
        services.AddScoped<ITokenRevocationService, TokenRevocationService>();
        services.AddScoped<IPasswordPolicyValidator, PasswordPolicyValidator>();
        
        // Memory Cache for token revocation and permissions
        services.AddMemoryCache(options =>
        {
            options.SizeLimit = 10000; // Max 10000 cached items
            options.CompactionPercentage = 0.25; // Remove 25% when limit hit
        });

        connectionString = ResolveConnectionString(configuration);
        services.AddMarten((IServiceProvider sp) =>
        {
            var options = new StoreOptions();
            options.Connection(connectionString);

            // Schema management. Marten's default (CreateOrUpdate) attempts a migration whenever a
            // write finds the schema out of date — so a schema mismatch surfaces as random 500s on
            // user requests, in a loop, since the failed migration is retried every write. That is
            // how a single→conjoined event-tenancy change (which is NOT a safe live migration) took
            // down content creation on a live instance.
            //
            // In production: None ("trust the schema") + ApplyAllDatabaseChangesOnStartup (chained
            // after AddMarten) applies changes ONCE at boot. A bad migration now fails the *deploy*
            // loudly instead of silently breaking writes for users. Development keeps CreateOrUpdate
            // for a frictionless local loop. NOTE: changing Events.TenancyStyle on an existing store
            // is not auto-migratable — it requires an event-store rebuild, never a live migration.
            var isDevelopment =
                Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") == "Development";
            options.AutoCreateSchemaObjects = isDevelopment
                ? JasperFx.AutoCreate.CreateOrUpdate
                : JasperFx.AutoCreate.None;

            // Conjoined multi-tenancy: every document and event stream is tagged with a tenant id and
            // auto-filtered by the session's tenant. Global identity/registry docs opt out below.
            options.Policies.AllDocumentsAreMultiTenanted();
            options.Events.TenancyStyle = Marten.Storage.TenancyStyle.Conjoined;

            // Configure document versioning and indexes
            options.Schema.For<Content>()
                .DocumentAlias("contents")
                .Index(x => x.ContentType)  // Frequently filtered
                .Index(x => x.CreatedAt)    // Frequently sorted
                .Index(x => x.UpdatedAt)
                .Index(x => x.Status)
                .Index(x => new { x.ContentType, x.CreatedAt })
                .Index(x => new { x.ContentType, x.Status }); // Composite for status filtering
            
            options.Schema.For<User>()
                .SingleTenanted() // global identity — a user exists once across all tenants
                .DocumentAlias("users")
                .Index(x => x.Username, idx => idx.IsUnique = true)
                .Index(x => x.Email, idx => idx.IsUnique = true);
            
            // Global (single-tenanted) platform + auth infrastructure. Identity, roles, tokens, OTP,
            // idempotency and settings live once across all tenants — otherwise per-club role
            // resolution (Membership references global role ids) and token revocation would silently
            // fail inside a club's partition. Only domain content below stays tenant-scoped.
            options.Schema.For<SystemSetting>()
                .SingleTenanted()
                .DocumentAlias("system_settings");

            options.Schema.For<Models.Role>()
                .SingleTenanted() // roles are global; per-tenant assignment lives on Membership
                .DocumentAlias("roles")
                .Index(x => x.Name, idx => idx.IsUnique = true);

            options.Schema.For<RefreshToken>()
                .SingleTenanted() // token lifecycle is global, independent of which club is in the URL
                .DocumentAlias("refresh_tokens")
                // Optimistic concurrency so a single refresh token cannot be rotated twice
                // concurrently (defeats refresh-token reuse/replay).
                .UseOptimisticConcurrency(true)
                .Index(x => x.Token, idx => idx.IsUnique = true)  // Index for fast lookup
                .Index(x => x.UserId)  // Index for user queries
                .Index(x => x.ExpiresAt);  // Index for cleanup queries

            options.Schema.For<RevokedToken>()
                .SingleTenanted() // a revoked token must be revoked everywhere
                .DocumentAlias("revoked_tokens")
                .Index(x => x.TokenJti, idx => idx.IsUnique = true)  // Index for fast revocation check
                .Index(x => x.ExpiresAt);  // Index for cleanup queries

            options.Schema.For<IdempotencyRecord>()
                .SingleTenanted()
                .DocumentAlias("idempotency_records")
                .Index(x => x.Key, idx => idx.IsUnique = true);  // Unique constraint prevents race condition

            options.Schema.For<OtpCode>()
                .SingleTenanted() // sign-in codes are keyed by global email, not by club
                .DocumentAlias("otp_codes")
                .Index(x => x.Email)
                .Index(x => x.ExpiresAt);

            // Multi-tenancy registry (global documents — not tenant-scoped).
            options.Schema.For<Models.Tenant>()
                .SingleTenanted() // the tenant registry itself is global
                .DocumentAlias("tenants")
                .Index(x => x.Slug, idx => idx.IsUnique = true);
            options.Schema.For<Models.Membership>()
                .SingleTenanted() // maps global users to tenants — necessarily cross-tenant
                .DocumentAlias("memberships")
                .Index(x => x.UserId)
                .Index(x => x.TenantSlug);

            // Register Workflow Projection (Async)
            options.Projections.Add(new WorkflowProjection(sp), JasperFx.Events.Projections.ProjectionLifecycle.Async);

            // Let each opted-in module register its own document types / indexes on the shared store.
            foreach (var module in modules)
                module.ConfigureMarten(options);

            return options;
        })
        .BuildSessionsWith<barakoCMS.Infrastructure.Multitenancy.TenantSessionFactory>(Microsoft.Extensions.DependencyInjection.ServiceLifetime.Scoped)
        .AddAsyncDaemon(JasperFx.Events.Daemon.DaemonMode.Solo)
        // Apply schema changes once at boot (paired with AutoCreate.None in prod above) so a schema
        // mismatch fails the deploy loudly instead of 500ing live writes. A no-op when the DB already
        // matches the model. In Development this runs under CreateOrUpdate, so it's equally harmless.
        .ApplyAllDatabaseChangesOnStartup();

        // services.AddHealthChecks()
        //    .AddNpgSql(configuration.GetConnectionString("DefaultConnection")!, tags: new[] { "db", "ready" });

        services.AddHttpClient("ExternalApi")
                .AddStandardResilienceHandler();

        // Defaults registered with TryAdd so an opted-in module or the host can substitute a real
        // provider (e.g. a Resend email module) without being clobbered by these mocks.
        services.TryAddScoped<barakoCMS.Core.Interfaces.IEmailService, barakoCMS.Infrastructure.Services.MockEmailService>();
        services.TryAddScoped<barakoCMS.Core.Interfaces.ISmsService, barakoCMS.Infrastructure.Services.MockSmsService>();
        services.AddScoped<barakoCMS.Core.Interfaces.ISensitivityService, barakoCMS.Infrastructure.Services.SensitivityService>();
        services.AddScoped<barakoCMS.Core.Interfaces.IOtpService, barakoCMS.Infrastructure.Services.OtpService>();
        // Device trust is opt-in: the default gate does nothing. The DeviceTrust module overrides it.
        services.TryAddScoped<barakoCMS.Core.Interfaces.IDeviceGate, barakoCMS.Core.Interfaces.NoopDeviceGate>();
        // Per-request tenant, resolved from the subdomain by TenantResolutionMiddleware.
        services.AddScoped<barakoCMS.Infrastructure.Multitenancy.TenantContext>();
        services.AddScoped<barakoCMS.Infrastructure.Services.IConfigurationService, barakoCMS.Infrastructure.Services.ConfigurationService>();

        // Workflow Action Plugins
        services.AddScoped<barakoCMS.Features.Workflows.IWorkflowAction, barakoCMS.Features.Workflows.Actions.EmailAction>();
        services.AddScoped<barakoCMS.Features.Workflows.IWorkflowAction, barakoCMS.Features.Workflows.Actions.SmsAction>();
        services.AddScoped<barakoCMS.Features.Workflows.IWorkflowAction, barakoCMS.Features.Workflows.Actions.WebhookAction>();
        services.AddScoped<barakoCMS.Features.Workflows.IWorkflowAction, barakoCMS.Features.Workflows.Actions.CreateTaskAction>();
        services.AddScoped<barakoCMS.Features.Workflows.IWorkflowAction, barakoCMS.Features.Workflows.Actions.UpdateFieldAction>();
        services.AddScoped<barakoCMS.Features.Workflows.IWorkflowAction, barakoCMS.Features.Workflows.Actions.ConditionalAction>();


        services.AddScoped<barakoCMS.Features.Workflows.WorkflowEngine>();
        services.AddScoped<barakoCMS.Features.Workflows.IWorkflowEngine>(sp => sp.GetRequiredService<barakoCMS.Features.Workflows.WorkflowEngine>());

        // Workflow Tools
        services.AddScoped<IWorkflowPluginRegistry, WorkflowPluginRegistry>();
        services.AddScoped<IWorkflowSchemaValidator, WorkflowSchemaValidator>();
        services.AddScoped<ITemplateVariableExtractor, TemplateVariableExtractor>();
        services.AddScoped<IWorkflowDebugger, WorkflowDebugger>();
        services.AddScoped<IContentValidatorService, ContentValidatorService>();
        services.AddScoped<IContentTypeValidatorService, ContentTypeValidatorService>();
        services.AddSingleton<IKubernetesMonitorService, KubernetesMonitorService>();
        services.AddSingleton<IMetricsService, MetricsService>();
        services.AddScoped<IBackupService, BackupService>();

        services.AddSingleton<FastEndpoints.IGlobalPreProcessor, barakoCMS.Infrastructure.Filters.IdempotencyFilter>();
        // Sensitivity is applied explicitly by the read endpoints (Get/List/History) via
        // ISensitivityService, not as a post-processor: a post-processor's edits did not reach the
        // serialized response, so field-level masking was silently dropped.

        // Background service for cleaning up expired tokens
        services.AddHostedService<TokenCleanupService>();

        // Rate Limiting
        services.AddRateLimiter(options =>
        {
            // Global rate limit: 100 requests per minute per IP
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                var ipAddress = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

                return RateLimitPartition.GetFixedWindowLimiter(ipAddress, _ =>
                    new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 100,
                        Window = TimeSpan.FromMinutes(1),
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 10
                    });
            });

            // Stricter limit for authentication endpoints: 5 per 15 minutes
            options.AddPolicy("auth", context =>
            {
                var ipAddress = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

                return RateLimitPartition.GetFixedWindowLimiter($"auth-{ipAddress}", _ =>
                    new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 5,  // 5 login attempts per 15 minutes
                        Window = TimeSpan.FromMinutes(15)
                    });
            });
            
            // Registration rate limit: 5 per hour
            options.AddPolicy("registration", context =>
            {
                var ipAddress = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

                return RateLimitPartition.GetFixedWindowLimiter($"registration-{ipAddress}", _ =>
                    new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 5,  // 5 registrations per hour
                        Window = TimeSpan.FromHours(1)
                    });
            });

            options.OnRejected = async (context, cancellationToken) =>
            {
                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await context.HttpContext.Response.WriteAsync(
                    "Too many requests. Please try again later.", cancellationToken);
            };
        });

        // Health Checks UI (Config-Gated)
        if (configuration.GetValue<bool>("HealthChecksUI:Enabled"))
        {
            services.AddHealthChecksUI(setup =>
            {
                setup.SetEvaluationTimeInSeconds(10); // Check every 10 seconds
                setup.MaximumHistoryEntriesPerEndpoint(60);
                setup.AddHealthCheckEndpoint("BarakoCMS", "/health");
            })
            .AddInMemoryStorage();
        }

        return services;
    }

    private static string ResolveConnectionString(IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection");
        var dbUrl = Environment.GetEnvironmentVariable("DATABASE_URL");

        if (!string.IsNullOrWhiteSpace(dbUrl))
        {
            try
            {
                var uri = new Uri(dbUrl);
                var userInfo = uri.UserInfo.Split(':');
                var username = userInfo[0];
                var password = userInfo.Length > 1 ? userInfo[1] : "";

                connectionString = $"Host={uri.Host};Port={uri.Port};Database={uri.AbsolutePath.TrimStart('/')};Username={username};Password={password};SSL Mode=Disable;Include Error Detail=true";
            }
            catch
            {
                connectionString = dbUrl;
            }
        }

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return "Server=127.0.0.1;Port=5432;Database=dummy;User Id=postgres;Password=nomartencrash;";
        }

        return connectionString;
    }

    public static IApplicationBuilder UseBarakoCMS(this IApplicationBuilder app)
    {
        var configuration = app.ApplicationServices.GetRequiredService<IConfiguration>();
        var env = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");

        // Global exception handler — MUST be first so it wraps every downstream middleware/endpoint.
        // Returns a structured 500 (no stack trace leak) and logs the exception via FastEndpoints.
        app.UseDefaultExceptionHandler();

        // HTTPS Redirection and HSTS (Production only)
        if (env != "Development")
        {
            app.UseHttpsRedirection();
            app.UseHsts();
        }

        // Security Headers
        app.Use(async (context, next) =>
        {
            // Prevent XSS attacks
            context.Response.Headers.Append("X-Content-Type-Options", "nosniff");
            context.Response.Headers.Append("X-Frame-Options", "DENY");
            context.Response.Headers.Append("X-XSS-Protection", "1; mode=block");
            context.Response.Headers.Append("Referrer-Policy", "strict-origin-when-cross-origin");

            // Content Security Policy
            context.Response.Headers.Append("Content-Security-Policy",
            "default-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self' data:;");

            // HSTS (HTTP Strict Transport Security)
            if (context.Request.IsHttps)
            {
                context.Response.Headers.Append("Strict-Transport-Security",
                    "max-age=31536000; includeSubDomains");
            }

            await next();
        });

        // Rate Limiting
        app.UseRateLimiter();

        // OBSERVABILITY MIDDLEWARE
        // 1. Correlation ID (Must be early to tag everything)
        app.UseMiddleware<barakoCMS.Infrastructure.Middleware.CorrelationIdMiddleware>();

        // 2. Request Logging (Must be after Correlation ID)
        app.UseMiddleware<barakoCMS.Infrastructure.Middleware.RequestResponseLoggingMiddleware>();

        // Resolve the tenant from the subdomain, early so downstream code can read it.
        app.UseMiddleware<barakoCMS.Infrastructure.Multitenancy.TenantResolutionMiddleware>();

        // CORS (Must be before Authentication/Authorization)
        app.UseCors("SecurePolicy");

        app.UseAuthentication();
        
        // Token Revocation Check (Must be after Authentication)
        app.UseMiddleware<barakoCMS.Infrastructure.Middleware.TokenValidationMiddleware>();

        // Reject tokens minted for a different tenant than the resolved host.
        app.UseMiddleware<barakoCMS.Infrastructure.Multitenancy.TenantAccessMiddleware>();

        app.UseAuthorization();
        // Global pre/post processors come from DI, so modules can contribute their own (e.g. the
        // DeviceTrust enforcement pre-processor) simply by registering IGlobalPreProcessor/PostProcessor.
        var globalPreProcessors = app.ApplicationServices.GetServices<FastEndpoints.IGlobalPreProcessor>().ToArray();
        var globalPostProcessors = app.ApplicationServices.GetServices<FastEndpoints.IGlobalPostProcessor>().ToArray();
        app.UseFastEndpoints(c =>
        {
            c.Errors.UseProblemDetails();
            c.Endpoints.Configurator = ep =>
            {
                if (globalPreProcessors.Length > 0)
                    ep.PreProcessors(Order.Before, globalPreProcessors);
                if (globalPostProcessors.Length > 0)
                    ep.PostProcessors(Order.After, globalPostProcessors);
            };
        });

        // Health Checks Endpoint — unauthenticated for k8s liveness/readiness probes.
        // All checks still run (status code reflects DB/disk/memory), but the response body is
        // minimal so anonymous callers can't enumerate internal check names/descriptions/timings.
        app.UseHealthChecks("/health", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
        {
            Predicate = _ => true,
            ResponseWriter = async (context, report) =>
            {
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync($"{{\"status\":\"{report.Status}\"}}");
            }
        });

        // Health Checks UI Dashboard (Config-Gated)
        if (configuration.GetValue<bool>("HealthChecksUI:Enabled"))
        {
            app.UseHealthChecksUI(options =>
            {
                options.UIPath = "/health-ui";
                options.ApiPath = "/health-ui-api";
            });
        }

        if (env == "Development")
        {
            app.UseSwaggerGen();
        }

        return app;
    }

    /// <summary>
    /// Runs <see cref="IBarakoModule.SeedAsync"/> for every registered module inside a single shared
    /// session, committing once at the end. Call after <c>UseBarakoCMS</c> during startup. No-op when
    /// no modules are registered.
    /// </summary>
    public static async Task RunBarakoModuleSeedersAsync(this IHost host, CancellationToken ct = default)
    {
        using var scope = host.Services.CreateScope();
        var modules = scope.ServiceProvider.GetServices<IBarakoModule>().ToList();
        if (modules.Count == 0)
            return;

        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        foreach (var module in modules)
            await module.SeedAsync(session, scope.ServiceProvider, ct);
        await session.SaveChangesAsync(ct);
    }
}
