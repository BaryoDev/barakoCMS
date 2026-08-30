using Serilog;
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

        // Sorted before anything runs, so DI registration, schema and seeding all see the same
        // order and a module that must configure after another actually does. Independent modules
        // keep their declared order.
        var modules = ModuleOrder.Sort(moduleBuilder.Modules);

        // Contract compatibility, checked before anything is registered. A module that states a
        // version core cannot honour is refused here rather than allowed to half-configure and fail
        // somewhere that does not name it.
        var unsupported = modules
            .Where(m => m.ContractVersion != 0
                        && (m.ContractVersion < ModuleContract.MinimumSupported
                            || m.ContractVersion > ModuleContract.Version))
            .Select(m => $"{m.Name} (declares contract v{m.ContractVersion})")
            .ToList();

        if (unsupported.Count > 0)
        {
            throw new InvalidOperationException(
                $"This barakoCMS implements module contract v{ModuleContract.Version} and supports "
                + $"v{ModuleContract.MinimumSupported} through v{ModuleContract.Version}. Refusing to load: "
                + string.Join(", ", unsupported)
                + ". Update the module, or run a core that implements its contract version.");
        }


        foreach (var module in modules)
        {
            // Keep the instance discoverable at runtime (used by the seed runner).
            services.AddSingleton<IBarakoModule>(module);
            module.ConfigureServices(services, ModuleConfiguration(configuration, module));
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
        // Config wins, and the environment supplies the default, so Development keeps Swagger with
        // no configuration at all while production stays off unless it is asked for. Defaulting to
        // false everywhere would have removed it for every developer.
        var swaggerOnByDefault =
            Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") == "Development";
        if (configuration.GetValue("Swagger:Enabled", swaggerOnByDefault))
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

        services.AddAuthenticationJwtBearer(
            s => s.SigningKey = jwtKey,
            o =>
            {
                var p = o.TokenValidationParameters;
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

        // Second auth scheme for machine callers: `Authorization: Bearer bcms_...` API keys. A policy
        // scheme sniffs the bearer token and forwards bcms_ tokens to the API-key handler, everything
        // else to the JWT handler — so both credential types work on the same endpoints and all
        // existing Roles()/permission checks apply to whichever principal comes out.
        const string smartScheme = "JwtOrApiKey";
        services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = smartScheme;
                options.DefaultChallengeScheme = smartScheme;
            })
            .AddScheme<barakoCMS.Infrastructure.Auth.ApiKeyAuthenticationOptions, barakoCMS.Infrastructure.Auth.ApiKeyAuthenticationHandler>(
                barakoCMS.Infrastructure.Auth.ApiKeyAuthenticationHandler.SchemeName, _ => { })
            .AddPolicyScheme(smartScheme, smartScheme, options =>
            {
                options.ForwardDefaultSelector = ctx =>
                {
                    var header = ctx.Request.Headers.Authorization.ToString();
                    return header.StartsWith("Bearer " + barakoCMS.Infrastructure.Auth.ApiKeyService.Prefix, StringComparison.Ordinal)
                        ? barakoCMS.Infrastructure.Auth.ApiKeyAuthenticationHandler.SchemeName
                        : Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme;
                };
            });
        services.AddSingleton<barakoCMS.Infrastructure.Auth.ApiKeyService>();

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

            // Marten 9 flips several event-store defaults (QuickWithServerTimestamps append,
            // bigint event columns, advanced async tracking). Two of those imply schema
            // migrations, and production runs AutoCreate.CreateOnly, which refuses live
            // migrations by design; the daemon would break on first append instead. Keep the
            // V8 behaviour this upgrade was tested against, and adopt the new defaults
            // deliberately, with a migration step, not as a side effect of a version bump.
            options.RestoreV8Defaults();

            // Not part of the V8 contract worth keeping: V8 forwarded Npgsql's internal
            // logger, which is pure noise next to Marten's own structured logs.
            options.DisableNpgsqlLogging = true;

            options.Connection(connectionString);

            // Schema management. Marten's default (CreateOrUpdate) attempts a migration whenever a
            // write finds the schema out of date — so a schema mismatch surfaces as random 500s on
            // user requests, in a loop, since the failed migration is retried every write. That is
            // how a single→conjoined event-tenancy change (which is NOT a safe live migration) took
            // down content creation on a live instance.
            //
            // Production uses CreateOnly (Marten's recommended prod setting): it creates missing
            // objects — so a fresh database and any document type not explicitly registered below
            // still work — but NEVER updates or drops an existing object, so it can't attempt the
            // failing live migration. None is too strict: it requires every document type to be
            // pre-registered and can't stand up a fresh database. Development keeps CreateOrUpdate for
            // a frictionless local loop. NOTE: changing Events.TenancyStyle on an existing store is
            // still not auto-migratable — it requires an event-store rebuild, never a live migration.
            var isDevelopment =
                Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") == "Development";
            options.AutoCreateSchemaObjects = isDevelopment
                ? JasperFx.AutoCreate.CreateOrUpdate
                : JasperFx.AutoCreate.CreateOnly;

            // Every `object`-typed JSON value (content Data bags, permission Conditions, audit
            // Metadata, workflow parameters) goes through ObjectJsonConverter so fractional numbers
            // land as decimal instead of double, and nested values are plain CLR types at every depth
            // rather than raw JsonElement. See that converter for the two real bugs this prevents.
            options.UseSystemTextJsonForSerialization(configure: json =>
            {
                json.Converters.Add(new barakoCMS.Infrastructure.Serialization.ObjectJsonConverter());
            });

            // Conjoined multi-tenancy: every document and event stream is tagged with a tenant id and
            // auto-filtered by the session's tenant. Global identity/registry docs opt out below.
            options.Policies.AllDocumentsAreMultiTenanted();
            options.Events.TenancyStyle = JasperFx.MultiTenancy.TenancyStyle.Conjoined;

            // Configure document versioning and indexes
            options.Schema.For<Content>()
                .DocumentAlias("contents")
                .Index(x => x.ContentType)  // Frequently filtered
                .Index(x => x.CreatedAt)    // Frequently sorted
                .Index(x => x.UpdatedAt)
                .Index(x => x.Status)
                .Index(x => new { x.ContentType, x.CreatedAt })
                .Index(x => new { x.ContentType, x.Status }); // Composite for status filtering
                // NOTE: no dedicated index on ScheduledPublishAt/ScheduledUnpublishAt. The scheduler
                // sweep leads with Status (indexed above), which is the selective predicate; the
                // schedule-time comparison is a cheap secondary filter. Adding indexes here would be a
                // delta on the existing mt_doc_contents table, which the prod/playground AutoCreate.
                // CreateOnly policy refuses at startup (there is no online-migration step yet — H.40).

            // Navigation menus are a "menu" content type served through public delivery, not a bespoke
            // doc. Keeping them as content keeps them pluggable and drops a whole CRUD surface. The old
            // Menu document + /api/menus endpoints were removed; existing "menus" tables are just left
            // orphaned (safe under AutoCreate.CreateOnly, which never alters or drops them).

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

            options.Schema.For<MfaSecret>()
                .SingleTenanted() // second factor is per global identity, like the user and OTP codes
                .DocumentAlias("mfa_secrets")
                // Serialize concurrent verifies so the replay guard can't be beaten by a race: two
                // requests with the same code can't both read the old LastUsedTimeStep and both win.
                .UseOptimisticConcurrency(true);

            options.Schema.For<ApiKey>()
                .SingleTenanted() // credentials are global, like users and tokens
                .DocumentAlias("api_keys")
                .Index(x => x.KeyHash, idx => idx.IsUnique = true) // hash lookup at auth time
                .Index(x => x.UserId)
                .Index(x => x.TenantSlug);

            options.Schema.For<Models.AuditEvent>()
                .SingleTenanted() // one global chain per tenant, kept as data like ApiKey.TenantSlug
                .DocumentAlias("audit_events")
                .Index(x => x.TenantSlug)
                .Index(x => x.CreatedAt)
                .Index(x => x.Action)
                .Index(x => x.ActorUserId)
                .Index(x => new { x.TenantSlug, x.CreatedAt }); // the RecordAsync "latest entry" lookup

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

            // Each module registers its own document types through a surface that only accepts
            // types it ships. ConfigureMarten still runs for modules that predate ConfigureSchema,
            // and is warned about, because removing it inside a major would break them silently.
            foreach (var module in modules)
            {
                module.ConfigureSchema(new ModuleSchema(options, module));

#pragma warning disable CS0618 // deliberately calling the obsolete hook during its deprecation window
                if (OverridesConfigureMarten(module))
                {
                    Log.Warning(
                        "Module {Module} uses the deprecated ConfigureMarten(StoreOptions), which can "
                        + "reach core's documents and the event store. Move to ConfigureSchema(IModuleSchema); "
                        + "ConfigureMarten is removed in barakoCMS 5.0.",
                        module.Name);
                }
                module.ConfigureMarten(options);
#pragma warning restore CS0618
            }

            return options;
        })
        .BuildSessionsWith<barakoCMS.Infrastructure.Multitenancy.TenantSessionFactory>(Microsoft.Extensions.DependencyInjection.ServiceLifetime.Scoped)
        // HotCold, not Solo. Solo assumes there is never more than one node, and every node that
        // starts under it runs every projection. Two instances therefore both process the same
        // events, and WorkflowProjection has external side effects: an email, an SMS, a webhook, a
        // created task. Scaling out sent every one of them twice.
        //
        // HotCold takes a Postgres advisory lock per projection so exactly one process runs each.
        .AddAsyncDaemon(JasperFx.Events.Daemon.DaemonMode.HotCold);
        // Schema is applied explicitly at startup via host.ApplyMartenSchemaAsync() (below), called
        // BEFORE the data seeders run. ApplyAllDatabaseChangesOnStartup() can't be used here: it
        // registers a hosted service that runs during app.Run(), but the seeders run before that, so
        // with CreateOnly's no-on-demand-DDL they'd hit tables that don't exist yet on a fresh
        // database.

        // services.AddHealthChecks()
        //    .AddNpgSql(configuration.GetConnectionString("DefaultConnection")!, tags: new[] { "db", "ready" });

        // AllowAutoRedirect defaults to true, and WebhookAction validates only the URL it was given.
        // A webhook target that answers 302 Location: http://169.254.169.254/... was therefore
        // followed to the metadata service with the block list never consulted for that address:
        // the SSRF guard covered the first hop only. That needs no DNS control and no race, unlike
        // the rebinding in #258, and works on the first attempt.
        //
        // A webhook receiver has no legitimate reason to redirect a delivery. If one is ever wanted,
        // the target has to go back through the guard before it is followed, never by the handler on
        // its own.
        //
        // The connect callback is the rest of it. Checking a host and then letting the handler
        // resolve the name again left the check describing one address and the connection going to
        // another (#258). The guard resolves once and opens the socket to an address that answer
        // survived, so the pre-flight check in WebhookAction is now an early refusal rather than the
        // thing standing between a workflow and the metadata service.
        services.AddSingleton(barakoCMS.Infrastructure.Http.OutboundAddressGuard.Default);
        services.AddHttpClient("ExternalApi")
                .ConfigurePrimaryHttpMessageHandler(sp => barakoCMS.Infrastructure.Http.OutboundHttpHandler.Create(
                    sp.GetRequiredService<barakoCMS.Infrastructure.Http.OutboundAddressGuard>()))
                .AddStandardResilienceHandler();

        // Defaults registered with TryAdd so an opted-in module or the host can substitute a real
        // provider (e.g. a Resend email module) without being clobbered by these mocks.
        services.TryAddScoped<barakoCMS.Core.Interfaces.IEmailService, barakoCMS.Infrastructure.Services.MockEmailService>();
        services.TryAddScoped<barakoCMS.Core.Interfaces.ISmsService, barakoCMS.Infrastructure.Services.MockSmsService>();
        services.AddScoped<barakoCMS.Core.Interfaces.ISensitivityService, barakoCMS.Infrastructure.Services.SensitivityService>();
        services.AddScoped<barakoCMS.Core.Interfaces.IContentWriter, barakoCMS.Infrastructure.Services.ContentWriter>();
        // Runs any per-content-type domain rules a module registered (IContentLifecycleHook), so a
        // domain with real invariants can still be modelled as ordinary content.
        services.AddScoped<barakoCMS.Infrastructure.Services.IContentLifecycleRunner, barakoCMS.Infrastructure.Services.ContentLifecycleRunner>();

        // Erasure policy. Validated here rather than at first use: the failure being guarded against
        // is an operator believing a mode is in force when it is not, and startup is the only moment
        // that belief is cheap to correct. See DECISIONS.md D9.
        var erasure = barakoCMS.Infrastructure.Erasure.ErasureOptions.FromConfiguration(configuration);
        erasure.Validate();
        services.AddSingleton(erasure);
        services.AddScoped<barakoCMS.Infrastructure.Erasure.IContentEraser, barakoCMS.Infrastructure.Erasure.ContentEraser>();
        services.AddScoped<barakoCMS.Core.Interfaces.IOtpService, barakoCMS.Infrastructure.Services.OtpService>();

        // MFA (TOTP): secret protection (AES-GCM) + enrollment/verification.
        services.AddSingleton<barakoCMS.Infrastructure.Auth.Mfa.IMfaSecretProtector, barakoCMS.Infrastructure.Auth.Mfa.MfaSecretProtector>();
        services.AddScoped<barakoCMS.Infrastructure.Auth.Mfa.IMfaService, barakoCMS.Infrastructure.Auth.Mfa.MfaService>();
        // Device trust is opt-in: the default gate does nothing. The DeviceTrust module overrides it.
        services.TryAddScoped<barakoCMS.Core.Interfaces.IDeviceGate, barakoCMS.Core.Interfaces.NoopDeviceGate>();
        // Per-request tenant, resolved from a registered custom domain or the subdomain by
        // TenantResolutionMiddleware.
        services.AddScoped<barakoCMS.Infrastructure.Multitenancy.TenantContext>();
        // Singleton because the domain map is cached and read on every request; a scoped source
        // would rebuild the cache lookup per request for no benefit.
        services.AddSingleton<barakoCMS.Infrastructure.Multitenancy.ITenantDomainSource,
                              barakoCMS.Infrastructure.Multitenancy.TenantDomainSource>();
        services.Configure<barakoCMS.Infrastructure.Multitenancy.MultitenancyOptions>(
            configuration.GetSection(barakoCMS.Infrastructure.Multitenancy.MultitenancyOptions.SectionName));
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
        // IBackupService and BackupService were removed in 4.0. Both were registered here and
        // called by nothing, repo-wide, so reading the codebase suggested the application backed
        // itself up. It did not: backup is scripts/backup-cron.sh, run by the deployment, and
        // restore is scripts/restore-check.sh's procedure. A registered service that claims a
        // capability nothing invokes is worse than no service, because it stops people looking.

        // Confines API-key callers to the content surface and enforces their scopes. A no-op for JWT
        // callers (they carry no scope claims).
        services.AddSingleton<FastEndpoints.IGlobalPreProcessor, barakoCMS.Infrastructure.Auth.ApiKeyScopeProcessor>();

        services.AddSingleton<FastEndpoints.IGlobalPreProcessor, barakoCMS.Infrastructure.Filters.IdempotencyFilter>();
        // The finalizer completes an idempotency claim on success or releases it on failure, so a
        // failed request stays retryable. See IdempotencyFilter.
        services.AddSingleton<FastEndpoints.IGlobalPostProcessor, barakoCMS.Infrastructure.Filters.IdempotencyFinalizer>();
        // Sensitivity is applied explicitly by the read endpoints (Get/List/History) via
        // ISensitivityService, not as a post-processor: a post-processor's edits did not reach the
        // serialized response, so field-level masking was silently dropped.

        // Background service for cleaning up expired tokens
        services.AddHostedService<TokenCleanupService>();

        // Background service that applies scheduled publish/unpublish across all tenants
        services.AddHostedService<barakoCMS.Infrastructure.Services.ScheduledContentService>();

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
            
            // Anonymous telemetry ingestion (browser error reports). Deliberately tighter than the
            // global limit: the endpoint is unauthenticated and each request fans out to one lookup per
            // item in the batch, so the global 100/min would allow a 20x amplification against the DB.
            options.AddPolicy("telemetry", context =>
            {
                var ipAddress = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

                return RateLimitPartition.GetFixedWindowLimiter($"telemetry-{ipAddress}", _ =>
                    new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 20,  // 20 batches per minute is far above real client behaviour
                        Window = TimeSpan.FromMinutes(1)
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
            // A dummy string turns "nobody configured a database" into a connection refused against
            // localhost, which surfaces long after startup as an unrelated failure. Name the missing
            // setting instead. It stays a dummy in Development, where design-time tooling and the
            // codegen pass need Marten to build a store without a database behind it.
            var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
            if (!string.Equals(environment, "Development", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "No database connection string. Set ConnectionStrings:DefaultConnection or the DATABASE_URL environment variable.");
            }

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
        var csp = barakoCMS.Infrastructure.Security.SecurityHeaders.ContentSecurityPolicy(env);

        app.Use(async (context, next) =>
        {
            // Prevent XSS attacks
            context.Response.Headers.Append("X-Content-Type-Options", "nosniff");
            context.Response.Headers.Append("X-Frame-Options", "DENY");
            context.Response.Headers.Append("X-XSS-Protection", "1; mode=block");
            context.Response.Headers.Append("Referrer-Policy", "strict-origin-when-cross-origin");

            // Content Security Policy
            context.Response.Headers.Append("Content-Security-Policy", csp);

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
            // AllowDuplicateErrors keeps every failure that shares a field name. Without it a
            // content type with three bad fields reports one of them, so the caller fixes it, posts
            // again and is told about the next one.
            c.Errors.UseProblemDetails(x => x.AllowDuplicateErrors = true);

            // Deserialize incoming Dictionary<string, object> bodies (a content entry's Data, a
            // permission rule's Conditions) exactly the way they are stored — see ObjectJsonConverter.
            // Without this the two halves disagree: money in a request body would arrive as double
            // while the same value round-trips from Postgres as decimal, and nested values would
            // arrive as raw JsonElement, so a lifecycle hook validating a request could not read the
            // payload it is meant to be guarding.
            c.Serializer.Options.Converters.Add(
                new barakoCMS.Infrastructure.Serialization.ObjectJsonConverter());

            // Enums cross the wire as names, not numbers. An int enum renumbers every client the
            // moment a member is inserted, and the admin had the numbering transcribed into its own
            // source to cope.
            //
            // This is the HTTP serializer only. The Marten one above must NOT get this converter:
            // documents are stored with Status as a number and mt_doc_contents_idx_status indexes
            // ((data ->> 'Status')::integer), so writing names there breaks the index cast and every
            // LINQ query that filters on it. Changing storage is a data migration, not a contract
            // change. Reading still accepts a number, so an existing caller keeps working.
            c.Serializer.Options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());

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

        if (configuration.GetValue("Swagger:Enabled", env == "Development"))
        {
            app.UseSwaggerGen();
        }

        return app;
    }

    /// <summary>
    /// Runs <see cref="IBarakoModule.SeedAsync"/> for every registered module, each in its own scope,
    /// session and transaction. Call after <c>UseBarakoCMS</c> during startup. No-op when no modules
    /// are registered.
    /// </summary>
    /// <remarks>
    /// A session per module, not one shared session, and the difference matters in three ways.
    ///
    /// One module throwing used to lose every module's seed, including the ones that had already
    /// succeeded, because a single <c>SaveChangesAsync</c> committed the lot at the end.
    ///
    /// A module calling <c>SaveChangesAsync</c> itself used to commit every other module's
    /// half-finished work, and nothing in the contract said it must not.
    ///
    /// A module could read and modify every other module's uncommitted seed data, because they
    /// shared one identity map. Harmless between first-party modules; not something to leave in
    /// place before third-party ones exist.
    ///
    /// Failures are isolated so one module cannot stop the others, and then rethrown together, so
    /// a host that does nothing fails loudly rather than starting up quietly half-seeded. A host
    /// that would rather continue catches it, which is what the Suite does.
    ///
    /// Cancellation is never treated as a module failure: it propagates immediately.
    /// </remarks>
    /// <exception cref="AggregateException">One or more modules threw. Every other module still ran.</exception>
    public static async Task RunBarakoModuleSeedersAsync(this IHost host, CancellationToken ct = default)
    {
        List<IBarakoModule> modules;
        using (var probe = host.Services.CreateScope())
        {
            modules = probe.ServiceProvider.GetServices<IBarakoModule>().ToList();
        }
        if (modules.Count == 0)
            return;

        var failures = new List<Exception>();

        foreach (var module in modules)
        {
            ct.ThrowIfCancellationRequested();

            // A fresh scope per module: IDocumentSession is scoped, so this is what actually gives
            // each module its own session rather than a shared identity map.
            using var scope = host.Services.CreateScope();
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

            try
            {
                await module.SeedAsync(session, scope.ServiceProvider, ct);
                await session.SaveChangesAsync(ct);
            }
            catch (OperationCanceledException)
            {
                throw; // shutting down is not a module fault
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Module {Module} failed to seed. Other modules were unaffected.", module.Name);
                failures.Add(new InvalidOperationException($"Module '{module.Name}' failed to seed.", ex));
            }
        }

        if (failures.Count > 0)
            throw new AggregateException($"{failures.Count} module seeder(s) failed.", failures);
    }

    /// <summary>
    /// Applies all outstanding Marten schema changes to the database, upfront. Call this at startup
    /// BEFORE any seeder runs. It's the deliberate, ordered replacement for
    /// ApplyAllDatabaseChangesOnStartup: because production runs AutoCreate.CreateOnly, which creates
    /// missing objects but never issues DDL on demand for an existing one, the schema must exist
    /// before the seeders query it, and the seeders run before app.Run(), so a boot-time hosted
    /// service is too late. Idempotent: a no-op when the DB already matches the model.
    /// A change CreateOnly refuses (anything needing an ALTER) throws here, failing the deploy loudly
    /// instead of 500ing live writes. That is the upgrade path's entry point: generate the delta with
    /// <c>db-patch</c>, review it, apply it, then deploy. See docs/upgrading-to-4.0.md.
    /// </summary>
    public static async Task ApplyMartenSchemaAsync(this IHost host)
    {
        var store = host.Services.GetRequiredService<Marten.IDocumentStore>();
        await store.Storage.ApplyAllConfiguredChangesToDatabaseAsync();
    }

    /// <summary>
    /// The configuration a module is allowed to see: its own <c>Modules:{Name}</c> section.
    /// </summary>
    /// <remarks>
    /// A module used to receive the application root, which carries <c>ConnectionStrings</c>,
    /// <c>JWT</c> and <c>InitialAdmin</c>. Nothing about the module contract needs any of those, so
    /// handing them over was authority granted by accident rather than on purpose.
    ///
    /// This does not make a hostile module impossible. In-process code can read the environment or
    /// the filesystem whatever this returns. It means a module that wants a core secret has to reach
    /// around the API to get it, which is both a signal and something a reviewer can grep for.
    ///
    /// The legacy fallback exists so upgrading does not silently un-configure a module that reads a
    /// root section today. It warns rather than failing, because failing to start is a worse outcome
    /// than running with a deprecation notice, and it names both keys so the fix is obvious.
    /// </remarks>
    internal static IConfiguration ModuleConfiguration(IConfiguration root, IBarakoModule module)
    {
        var scopedKey = $"{ModulesConfigurationSection}:{module.Name}";
        var scoped = root.GetSection(scopedKey);

        var legacyKey = module.LegacyConfigurationSection;
        if (string.IsNullOrWhiteSpace(legacyKey))
            return scoped;

        var legacy = root.GetSection(legacyKey);
        if (!legacy.Exists())
            return scoped;

        if (!scoped.Exists())
        {
            Log.Warning(
                "Module {Module} is reading configuration from the deprecated root section {Legacy}. "
                + "Move those settings under {Scoped}. The root section stops being read in a future "
                + "major version.",
                module.Name, legacyKey, scopedKey);

            return legacy;
        }

        // Both present: a half-finished migration. Picking one whole section would silently discard
        // every key left behind in the other, and the module would run misconfigured with nothing
        // said. Layered instead, so each key resolves and the scoped value wins where both define it.
        Log.Warning(
            "Module {Module} has settings in both {Scoped} and the deprecated {Legacy}. Keys are being "
            + "merged with {Scoped} winning. Finish moving them: the root section stops being read in "
            + "a future major version.",
            module.Name, scopedKey, legacyKey);

        return new ConfigurationBuilder()
            .AddConfiguration(legacy)
            .AddConfiguration(scoped)  // added last, so it wins per key
            .Build();
    }

    /// <summary>Root key under which every module's own settings live.</summary>
    internal const string ModulesConfigurationSection = "Modules";

    /// <summary>
    /// Whether a module actually implements the deprecated hook, rather than inheriting the
    /// interface's no-op default.
    /// </summary>
    /// <remarks>
    /// Checked through the interface map rather than <c>GetMethod</c>: a default interface
    /// implementation is not a member of the implementing type, so <c>GetMethod("ConfigureMarten")</c>
    /// returns null both for a module that did not override it and for one that implemented it
    /// explicitly. The map says which method actually runs.
    ///
    /// Only decides whether to warn. Getting it wrong costs a log line, never behaviour.
    /// </remarks>
    internal static bool OverridesConfigureMarten(IBarakoModule module)
    {
        var map = module.GetType().GetInterfaceMap(typeof(IBarakoModule));
        for (var i = 0; i < map.InterfaceMethods.Length; i++)
        {
            if (map.InterfaceMethods[i].Name != nameof(IBarakoModule.ConfigureMarten))
                continue;
            return map.TargetMethods[i].DeclaringType != typeof(IBarakoModule);
        }
        return false;
    }
}
