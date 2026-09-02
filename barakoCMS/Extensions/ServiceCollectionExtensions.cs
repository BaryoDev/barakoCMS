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
            services.SwaggerDocument(o =>
            {
                // FastEndpoints tags by path segment, and every route here starts /api/, so all but
                // the three endpoints that tag themselves landed on one tag: "Api". A generator
                // groups methods by tag, so that document generates one class with every method on
                // it. Off, and NamespaceTagProcessor tags by namespace instead.
                o.AutoTagPathSegmentIndex = 0;
                o.DocumentSettings = s =>
                    s.OperationProcessors.Add(new barakoCMS.Infrastructure.OpenApi.NamespaceTagProcessor());
            });
        }

        // Holds the rendered OpenAPI document per tenant. Registered whether or not Swagger is on,
        // because the content-type endpoints invalidate it and a constructor dependency that exists
        // only under a config flag is a startup failure waiting for the first deployment that turns
        // the flag off. Nothing populates it when Swagger is off, so it costs an empty dictionary.
        services.AddSingleton<barakoCMS.Infrastructure.OpenApi.DeliveryDocumentCache>();

        var connectionString = ResolveConnectionString(configuration);

        // Thresholds are configurable. The memory default is deliberately generous:
        // .NET's server GC holds ~1.3GB of private memory on an idle container, so a
        // 1GB ceiling reports Unhealthy on a perfectly healthy boot.
        var maxMemoryMb = configuration.GetValue<long?>("HealthChecks:MaxPrivateMemoryMegabytes") ?? 4096;
        var minFreeDiskMb = configuration.GetValue<long?>("HealthChecks:MinimumFreeDiskMegabytes") ?? 512;

        // Liveness answers "is this process wedged, restart it". Readiness answers "can this process
        // serve traffic right now". Only a check a restart can actually fix belongs on liveness, so
        // the tags split like this:
        //
        //   live  : Memory                         a process past its private-memory ceiling is
        //                                          exactly what a restart clears.
        //   ready : Database, Disk Space, Memory,  none of these is fixed by killing the process,
        //           Startup Seeding                and the database one is shared, so tagging it
        //                                          live would restart every replica at once on a
        //                                          single Postgres blip.
        //
        // See issue #281.
        services.AddSingleton<barakoCMS.Infrastructure.Health.StartupSeedGate>();

        // A stopped projection shard halts every workflow and is invisible to the checks above:
        // the database is up, the disk is fine, memory is fine, and nothing fires. Degraded rather
        // than Unhealthy on purpose; see ProjectionLag.
        var maxProjectionLag = configuration.GetValue<long?>("HealthChecks:MaxProjectionLagEvents")
            ?? barakoCMS.Infrastructure.Health.ProjectionLag.DefaultTolerance;

        services.AddHealthChecks()
            .AddNpgSql(connectionString, name: "Database", tags: new[] { "db", "ready" })
            .AddDiskStorageHealthCheck(setup =>
            {
                setup.AddDrive(@"/", minimumFreeMegabytes: minFreeDiskMb);
                setup.CheckAllDrives = false;
            }, name: "Disk Space", tags: new[] { "disk", "ready" })
            .AddPrivateMemoryHealthCheck(
                maxMemoryMb * 1024 * 1024,
                name: "Memory",
                tags: new[] { "memory", "live", "ready" })
            .AddCheck<barakoCMS.Infrastructure.Health.StartupSeedHealthCheck>(
                "Startup Seeding",
                tags: new[] { "seed", "ready" })
            // Neither live nor ready on purpose. A halted shard is not fixed by killing this
            // process, and it reports Degraded rather than Unhealthy, so putting it on readiness
            // would risk pulling every replica out of service over workflow lag. It is here to be
            // seen on the health page, not to gate traffic. See ProjectionLag.
            .AddCheck<barakoCMS.Infrastructure.Health.WorkflowProjectionHealthCheck>(
                barakoCMS.Infrastructure.Health.WorkflowProjectionHealthCheck.Name,
                tags: new[] { "workflow", "projection" });

        services.AddSingleton(sp => new barakoCMS.Infrastructure.Health.WorkflowProjectionHealthCheck(
            sp.GetRequiredService<IDocumentStore>(), maxProjectionLag));

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

        // Strict-Transport-Security. Registered here, applied by UseHsts outside Development.
        services.AddHsts(options =>
            barakoCMS.Infrastructure.Security.HstsPolicy.Configure(options, configuration));

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
        services.AddScoped<ISessionEpochService, SessionEpochService>();
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

            // The name is the lookup key for a content type: ContentValidatorService, SensitivityService
            // and the search-text backfill all resolve a definition by it, and each resolved a
            // duplicate differently. Uniqueness was enforced only by a read before the write, so two
            // concurrent creates both missed the read and both inserted. PerTenant, not global: under
            // conjoined tenancy one customer's "article" must not block another's.
            //
            // On an existing database this index is NOT created: production runs AutoCreate.CreateOnly,
            // which never alters an object that already exists. Such a store keeps today's
            // read-then-write behaviour until the index is applied by hand. See
            // migrations/4.0.0/3.x-to-4.0.sql, which also finds the duplicates that would make the
            // CREATE UNIQUE INDEX fail.
            // The sourcing decision, keyed by the content type NAME rather than by the definition's
            // id, so deleting a type and creating it again finds the standing answer instead of
            // arriving at the opposite one. Tenant-scoped like the definitions it describes: one
            // customer's "article" being event sourced says nothing about another's.
            options.Schema.For<ContentTypeSourcingPolicy>()
                .DocumentAlias("content_type_sourcing_policies")
                .Identity(x => x.Name);

            options.Schema.For<ContentTypeDefinition>()
                .Index(x => x.Name, idx =>
                {
                    idx.IsUnique = true;
                    idx.TenancyScope = Marten.Schema.Indexing.Unique.TenancyScope.PerTenant;
                });

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

            // Conjoined multi-tenant, deliberately, unlike the settings documents above. A credential
            // belongs to the tenant that added it, and one tenant's admin reaching another's is the
            // exact failure #287 found in the daemon.
            options.Schema.For<Connector>()
                .MultiTenanted()
                .DocumentAlias("connectors")
                .Index(x => x.Slug, idx =>
                {
                    idx.IsUnique = true;
                    // PerTenant, or the index is global and the first tenant to take "company-jira"
                    // stops every other tenant using that name. Marten does not infer this from the
                    // document being multi-tenanted, which is why ContentTypeDefinition says it too.
                    idx.TenancyScope = Marten.Schema.Indexing.Unique.TenancyScope.PerTenant;
                });

            options.Schema.For<RequestDefinition>()
                .MultiTenanted()
                .DocumentAlias("request_definitions")
                .Index(x => x.Slug, idx =>
                {
                    idx.IsUnique = true;
                    idx.TenancyScope = Marten.Schema.Indexing.Unique.TenancyScope.PerTenant;
                });

            options.Schema.For<ConnectorSecret>()
                .MultiTenanted()
                .DocumentAlias("connector_secrets")
                .Index(x => x.ConnectorId);

            options.Schema.For<EmailSettings>()
                .SingleTenanted() // one mail provider for the deployment, not one per tenant
                .DocumentAlias("email_settings");

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
                // Same reason RefreshToken and MfaSecret above have it, and this one was the odd
                // one out. Consuming a code is a read, a check and a write with nothing between
                // them, so two requests carrying the same code could both see Consumed still false
                // and both mint tokens. Device approval and passwordless sign-in both rest on this
                // path, and the login endpoint next door already uses an atomic Patch().Increment
                // for exactly this class of race.
                .UseOptimisticConcurrency(true)
                .Index(x => x.Email)
                .Index(x => x.ExpiresAt);

            options.Schema.For<PendingRegistration>()
                .SingleTenanted() // a registration is for a global identity, like the user it becomes
                .DocumentAlias("pending_registrations")
                // Same reason OtpCode above has it. Consuming a token is a read, a check and a
                // write with nothing between them, and two requests carrying one token must not both
                // create an account.
                .UseOptimisticConcurrency(true)
                // No unique index on Username or Email, deliberately. Reserving either before
                // anybody proved the address would let an unauthenticated caller hold names and
                // block addresses without owning a mailbox. Uniqueness is enforced where it counts,
                // on the users table, and re-checked at verification.
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
        // A proxy would resolve and connect to the target itself, so the guard would be inspecting
        // the hop to the proxy rather than the destination. Off unless an operator says otherwise,
        // because a system proxy can arrive from an environment variable nobody chose.
        var allowWebhookProxy = configuration.GetValue("Webhooks:AllowProxy", false);

        services.AddHttpClient("ExternalApi")
                .ConfigurePrimaryHttpMessageHandler(sp => barakoCMS.Infrastructure.Http.OutboundHttpHandler.Create(
                    sp.GetRequiredService<barakoCMS.Infrastructure.Http.OutboundAddressGuard>(),
                    allowWebhookProxy))
                .AddStandardResilienceHandler();

        // Defaults registered with TryAdd so an opted-in module or the host can substitute a real
        // provider (e.g. a Resend email module) without being clobbered by these mocks.
        services.TryAddScoped<barakoCMS.Core.Interfaces.IEmailService, barakoCMS.Infrastructure.Services.MockEmailService>();
        services.TryAddScoped<barakoCMS.Core.Interfaces.ISmsService, barakoCMS.Infrastructure.Services.MockSmsService>();
        services.AddScoped<barakoCMS.Core.Interfaces.ISensitivityService, barakoCMS.Infrastructure.Services.SensitivityService>();
        services.AddScoped<barakoCMS.Core.Interfaces.IContentSourcingPolicy, barakoCMS.Infrastructure.Services.ContentSourcingPolicyService>();
        // Constructed by hand rather than by type, so the configuration-reading constructor is the
        // one that runs. Both constructors are satisfiable from the container and the selection would
        // otherwise be a container detail, which is how EventSourcing:DocumentTypesAppend would end
        // up being a setting nothing reads.
        services.AddScoped<barakoCMS.Core.Interfaces.IContentWriter>(sp =>
            new barakoCMS.Infrastructure.Services.ContentWriter(
                sp.GetRequiredService<IDocumentSession>(),
                sp.GetRequiredService<barakoCMS.Core.Interfaces.IContentSourcingPolicy>(),
                sp.GetRequiredService<IConfiguration>()));
        services.AddScoped<barakoCMS.Infrastructure.Services.IContentRebuilder, barakoCMS.Infrastructure.Services.ContentRebuilder>();
        // Runs any per-content-type domain rules a module registered (IContentLifecycleHook), so a
        // domain with real invariants can still be modelled as ordinary content.
        services.AddScoped<barakoCMS.Infrastructure.Services.IContentLifecycleRunner, barakoCMS.Infrastructure.Services.ContentLifecycleRunner>();

        // Erasure policy. Validated here rather than at first use: the failure being guarded against
        // is an operator believing a mode is in force when it is not, and startup is the only moment
        // that belief is cheap to correct. See DECISIONS.md D9.
        var erasure = barakoCMS.Infrastructure.Erasure.ErasureOptions.FromConfiguration(configuration);
        erasure.Validate();

        // Connectors hold live third-party credentials, so a key that is present and wrong is
        // refused before the host is built rather than at the first send. An absent key is not an
        // error: it means the feature is off, and the endpoints say so with the setting named.
        barakoCMS.Infrastructure.Connectors.ConnectorOptions.FromConfiguration(configuration).Validate(configuration);
        services.AddSingleton(erasure);
        services.AddScoped<barakoCMS.Infrastructure.Erasure.IContentEraser, barakoCMS.Infrastructure.Erasure.ContentEraser>();
        services.AddScoped<barakoCMS.Core.Interfaces.IOtpService, barakoCMS.Infrastructure.Services.OtpService>();

        // Email verification for self-registration. Validated at startup for the same reason erasure
        // is: an operator who turned verification off has to have said so, because the failure is a
        // deployment that believes registration proves an address while it does not. See
        // DECISIONS.md D10.
        var emailVerification = barakoCMS.Infrastructure.Auth.EmailVerificationOptions.FromConfiguration(configuration);
        emailVerification.Validate();
        services.AddSingleton(emailVerification);
        services.AddScoped<barakoCMS.Core.Interfaces.IEmailVerificationService,
                           barakoCMS.Infrastructure.Services.EmailVerificationService>();

        // MFA (TOTP): secret protection (AES-GCM) + enrollment/verification.
        services.AddSingleton<barakoCMS.Infrastructure.Auth.Mfa.IMfaSecretProtector, barakoCMS.Infrastructure.Auth.Mfa.MfaSecretProtector>();
        services.AddSingleton<barakoCMS.Infrastructure.Security.ISecretProtector, barakoCMS.Infrastructure.Security.SecretProtector>();
        services.AddScoped<barakoCMS.Core.Interfaces.IEmailSettingsProvider, barakoCMS.Infrastructure.Services.EmailSettingsProvider>();
        services.AddSingleton<barakoCMS.Infrastructure.Connectors.IConnectorSecretProtector, barakoCMS.Infrastructure.Connectors.ConnectorSecretProtector>();
        services.AddScoped<barakoCMS.Infrastructure.Connectors.IConnectorSender, barakoCMS.Infrastructure.Connectors.ConnectorSender>();
        services.AddScoped<barakoCMS.Infrastructure.Connectors.IRequestComposer, barakoCMS.Infrastructure.Connectors.RequestComposer>();
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
        services.AddScoped<barakoCMS.Features.Workflows.IWorkflowAction, barakoCMS.Features.Workflows.Actions.RequestAction>();
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

        // Enforces the capability an endpoint declares with Definition.RequireCapability(...). A
        // no-op for endpoints that still gate on Roles(...).
        services.AddSingleton<FastEndpoints.IGlobalPreProcessor, barakoCMS.Infrastructure.Auth.CapabilityGateProcessor>();

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

        // Forwarded headers. Off unless configured, because reading X-Forwarded-For from an
        // untrusted peer would let a caller choose the IP the rate limiter partitions on.
        if (barakoCMS.Infrastructure.Security.ForwardedHeadersSetup.IsEnabled(configuration))
        {
            // Run the same parse once here so a bad proxy list stops the host at startup rather
            // than on the first request that happens to resolve the options.
            barakoCMS.Infrastructure.Security.ForwardedHeadersSetup.Configure(
                new Microsoft.AspNetCore.Builder.ForwardedHeadersOptions(), configuration);

            services.Configure<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>(
                options => barakoCMS.Infrastructure.Security.ForwardedHeadersSetup.Configure(options, configuration));
        }

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

    private static readonly Dictionary<string, string> SslModeMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["disable"] = "Disable",
        ["allow"] = "Allow",
        ["prefer"] = "Prefer",
        ["require"] = "Require",
        ["verify-ca"] = "VerifyCA",
        ["verifyca"] = "VerifyCA",
        ["verify-full"] = "VerifyFull",
        ["verifyfull"] = "VerifyFull"
    };

    private static bool IsDevelopmentEnvironment() =>
        IsDevelopment(Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"));

    /// <summary>Whether an environment name means Development.</summary>
    /// <remarks>
    /// Separated from the variable it usually reads so the mapping itself can be asserted. The
    /// decision it feeds is which environments get a dummy connection string and which get
    /// parameter values in exception messages, and neither was covered by a test that named an
    /// environment: the callers took a bool and nothing checked what produced it.
    /// </remarks>
    internal static bool IsDevelopment(string? environmentName) =>
        string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase);

    internal static string ResolveConnectionString(IConfiguration configuration) =>
        ResolveConnectionString(configuration, IsDevelopmentEnvironment());

    /// <summary>
    /// Resolves the connection string, taking the Development decision as an argument.
    /// </summary>
    /// <remarks>
    /// The flag is a parameter so a unit test can assert both halves without reading
    /// ASPNETCORE_ENVIRONMENT. IntegrationTestFixture sets that variable process-wide in its
    /// constructor and xUnit runs collections in parallel, so a test that reads it is really
    /// asserting which collection started first.
    /// </remarks>
    internal static string ResolveConnectionString(IConfiguration configuration, bool isDevelopment)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection");
        var dbUrl = configuration["DATABASE_URL"];

        if (!string.IsNullOrWhiteSpace(dbUrl))
        {
            try
            {
                var uri = new Uri(dbUrl);
                var colonIndex = uri.UserInfo.IndexOf(':');
                var rawUsername = colonIndex >= 0 ? uri.UserInfo[..colonIndex] : uri.UserInfo;
                var rawPassword = colonIndex >= 0 ? uri.UserInfo[(colonIndex + 1)..] : "";
                var username = Uri.UnescapeDataString(rawUsername);
                var password = Uri.UnescapeDataString(rawPassword);
                var database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
                var port = uri.Port > 0 ? uri.Port : 5432;

                var sslMode = "Require";
                if (!string.IsNullOrWhiteSpace(uri.Query))
                {
                    var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
                    foreach (var param in query)
                    {
                        var parts = param.Split('=', 2);
                        if (string.Equals(parts[0], "sslmode", StringComparison.OrdinalIgnoreCase))
                        {
                            var rawMode = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "";
                            if (!SslModeMap.TryGetValue(rawMode, out var mappedMode))
                            {
                                throw new ArgumentException($"Invalid sslmode '{rawMode}' in DATABASE_URL.");
                            }
                            sslMode = mappedMode;
                            break;
                        }
                    }
                }

                // Built rather than interpolated. Decoding the credentials above is correct and it
                // makes a case reachable that was not before: a semicolon is legal in a Postgres
                // password, percent-encoding it in the URL is how you are supposed to express one,
                // and UnescapeDataString turns %3B back into a literal ';'. Interpolated, that ends
                // the Password key and the rest of the password is read as another setting, so the
                // deployment fails to connect with a message about an unknown keyword rather than a
                // bad password. The builder escapes it.
                var builder = new Npgsql.NpgsqlConnectionStringBuilder
                {
                    Host = uri.Host,
                    Port = port,
                    Database = database,
                    Username = username,
                    Password = password,
                    SslMode = Enum.Parse<Npgsql.SslMode>(sslMode, ignoreCase: true),
                    // Npgsql puts parameter values into exception messages with this on, and those
                    // messages reach Serilog and whatever ships logs onward. DATABASE_URL is the
                    // convention managed providers use, so this path is the production one: on
                    // there, a failed insert copies the row's personal data into a store with a
                    // different retention policy and a different access list. See #449.
                    IncludeErrorDetail = isDevelopment,
                };

                connectionString = builder.ConnectionString;
            }
            catch (UriFormatException)
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
            if (!isDevelopment)
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

        // Forwarded headers, before anything that reads the client IP or the scheme. Only added
        // when ForwardedHeaders:Enabled names a trusted proxy; see ForwardedHeadersSetup.
        if (barakoCMS.Infrastructure.Security.ForwardedHeadersSetup.IsEnabled(configuration))
        {
            app.UseForwardedHeaders();
        }

        // HTTPS Redirection and HSTS (Production only)
        if (env != "Development")
        {
            app.UseHttpsRedirection();
            app.UseHsts();
        }

        // Security Headers
        var csp = barakoCMS.Infrastructure.Security.SecurityHeaders.ContentSecurityPolicy(env);
        var healthDashboardCsp =
            barakoCMS.Infrastructure.Security.SecurityHeaders.HealthDashboardContentSecurityPolicy(env);
        var healthDashboardEnabled = configuration.GetValue<bool>("HealthChecksUI:Enabled");

        app.Use(async (context, next) =>
        {
            context.Response.Headers.Append("X-Content-Type-Options", "nosniff");
            context.Response.Headers.Append("X-Frame-Options", "DENY");
            context.Response.Headers.Append("Referrer-Policy", "strict-origin-when-cross-origin");

            // X-XSS-Protection is deliberately not written. Every current browser ignores it, and
            // the auditor it was there to satisfy is not a threat model. While it was honoured its
            // filter introduced holes of its own: "1; mode=block" gave a cross-origin attacker a
            // way to detect content on the page by watching which loads were blocked. The CSP
            // below is the control that actually applies. See issue #271.

            // Content Security Policy. The looser style-src is reached only by the health dashboard,
            // and only while the dashboard is switched on.
            var policy = healthDashboardEnabled &&
                         barakoCMS.Infrastructure.Security.SecurityHeaders.IsHealthDashboardPath(context.Request.Path)
                ? healthDashboardCsp
                : csp;
            context.Response.Headers.Append("Content-Security-Policy", policy);

            // Strict-Transport-Security is NOT written here. UseHsts above owns it, configured by
            // HstsPolicy. This block used to append a second copy of the header on every HTTPS
            // request, in every environment: browsers take the first value and ignore the rest, so
            // the effective policy was the framework default rather than the one written here, and a
            // developer on https://localhost was being pinned too.

            await next();
        });

        // The Prometheus endpoint is mapped by the host (barakoCMS/Program.cs) and publishes route
        // names, per-endpoint traffic and process internals. It is guarded here, before endpoint
        // routing can execute it, rather than at the mapping. A scraper cannot sign in, so the
        // credential is the shared Metrics:ScrapeKey; with none set the endpoint serves nobody.
        var metricsScrapeKey = configuration[barakoCMS.Infrastructure.Security.MetricsScrapeAccess.ConfigurationKey];

        app.Use(async (context, next) =>
        {
            if (!barakoCMS.Infrastructure.Security.MetricsScrapeAccess.IsMetricsPath(context.Request.Path))
            {
                await next();
                return;
            }

            var presented = barakoCMS.Infrastructure.Security.MetricsScrapeAccess.PresentedKey(
                context.Request.Headers[barakoCMS.Infrastructure.Security.MetricsScrapeAccess.HeaderName],
                context.Request.Headers.Authorization);

            switch (barakoCMS.Infrastructure.Security.MetricsScrapeAccess.Authorize(metricsScrapeKey, presented))
            {
                case barakoCMS.Infrastructure.Security.MetricsScrapeDecision.Allowed:
                    await next();
                    return;

                case barakoCMS.Infrastructure.Security.MetricsScrapeDecision.Rejected:
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    context.Response.Headers.WWWAuthenticate = "Bearer";
                    return;

                default:
                    // Nothing is configured, so as far as a caller is concerned there is no endpoint.
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
            }
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

        // Health check endpoints, unauthenticated because kubelet cannot present a token. The
        // response body stays minimal on all three so anonymous callers cannot enumerate internal
        // check names, descriptions or timings.
        //
        // Three endpoints, not one. UseHealthChecks maps a path prefix, so the more specific paths
        // have to be registered first or "/health" swallows them.
        //
        //   /health/live   the liveness probe. Process-only. A failure here means restart me.
        //   /health/ready  the readiness probe. Database, disk, and the startup seed. A failure
        //                  here means take me out of rotation and leave me running.
        //   /health/build  which build is answering. Not a check; see below.
        //   /health        the full report, for humans and dashboards.
        //
        // Pointing liveness at the full report is what turned a Postgres restart into a
        // simultaneous restart of every replica. See issue #281.
        static Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions Probe(
            Func<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckRegistration, bool> predicate) =>
            new()
            {
                Predicate = predicate,
                ResponseWriter = async (context, report) =>
                {
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync($"{{\"status\":\"{report.Status}\"}}");
                }
            };

        // Which build is answering, as the commit it was built from. Anonymous, like the probes,
        // and for the same reason: the caller is a deploy pipeline, not a signed-in user.
        //
        // A release used to prove a deploy by asking for a 200 and reading back a version string,
        // and a version string cannot tell two builds apart. Today's 3.20.2 and yesterday's 3.20.2
        // are the same characters. A deploy that pulled nothing and restarted nothing answers that
        // check exactly like a deploy that worked. A commit sha cannot (#157).
        //
        // Stamped in at image build time (BARAKO_BUILD_SHA), not read from assembly metadata:
        // .git is in .dockerignore, so SourceLink has nothing to stamp inside the image. Unset
        // means "unknown", which fails the comparison rather than passing it.
        //
        // Its own path rather than a field on /health, so the probe body stays exactly what every
        // dashboard and kubelet already parses.
        var buildSha = Environment.GetEnvironmentVariable("BARAKO_BUILD_SHA");
        if (string.IsNullOrWhiteSpace(buildSha))
        {
            buildSha = "unknown";
        }

        // Serialized rather than interpolated: the value comes from the environment, and a quote in
        // it would otherwise produce a body that is not JSON.
        var buildBody = System.Text.Json.JsonSerializer.Serialize(new { sha = buildSha });

        app.Map("/health/build", branch => branch.Run(async context =>
        {
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(buildBody);
        }));

        app.UseHealthChecks("/health/live", Probe(check => check.Tags.Contains("live")));
        app.UseHealthChecks("/health/ready", Probe(check => check.Tags.Contains("ready")));
        app.UseHealthChecks("/health", Probe(_ => true));

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
            // Before UseSwaggerGen, because it rewrites that middleware's response: content types
            // are created at runtime, so /api/public/students can only reach the document here.
            app.UseMiddleware<barakoCMS.Infrastructure.OpenApi.DeliveryDocumentMiddleware>();
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
