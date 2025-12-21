using FastEndpoints;
using FastEndpoints.Swagger;
using FastEndpoints.Security;
using Marten;
using Marten.Events.Projections;
using Marten.Events.Daemon;
using Weasel.Core;
using barakoCMS.Features.Workflows;
using barakoCMS.Models;
using barakoCMS.Repository;
using System.Threading.RateLimiting;
using barakoCMS.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Builder;
using JasperFx.Events;
using JasperFx.Core;

namespace barakoCMS.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddBarakoCMS(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddFastEndpoints();
        if (Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") == "Development")
        {
            services.SwaggerDocument();
        }

        var connectionString = ResolveConnectionString(configuration);

        services.AddHealthChecks()
            .AddNpgSql(connectionString, name: "Database", tags: new[] { "db", "ready" })
            .AddDiskStorageHealthCheck(setup =>
            {
                setup.AddDrive(@"/", minimumFreeMegabytes: 512); // Warn if < 512MB free
                setup.CheckAllDrives = false;
            }, name: "Disk Space")
            .AddPrivateMemoryHealthCheck(1024 * 1024 * 1024, name: "Memory"); // 1GB threshold

        services.AddJWTBearerAuth(configuration["JWT:Key"]!, tokenValidation: p =>
        {
            p.ValidateIssuerSigningKey = true;
            p.ValidateIssuer = true;
            p.ValidateAudience = true;
            p.ValidIssuer = configuration["JWT:Issuer"];
            p.ValidAudience = configuration["JWT:Audience"];

            // Explicitly map claims
            p.NameClaimType = "Username";
            p.RoleClaimType = System.Security.Claims.ClaimTypes.Role;
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
                .DocumentAlias("users")
                .Index(x => x.Username, idx => idx.IsUnique = true)
                .Index(x => x.Email, idx => idx.IsUnique = true);
            
            options.Schema.For<SystemSetting>().DocumentAlias("system_settings");
            
            options.Schema.For<Models.Role>()
                .DocumentAlias("roles")
                .Index(x => x.Name, idx => idx.IsUnique = true);
            
            options.Schema.For<RefreshToken>().DocumentAlias("refresh_tokens");
            options.Schema.For<RevokedToken>().DocumentAlias("revoked_tokens");

            // Register Workflow Projection (Async)
            options.Projections.Add(new WorkflowProjection(sp), JasperFx.Events.Projections.ProjectionLifecycle.Async);

            return options;
        })
        .UseLightweightSessions()
        .AddAsyncDaemon(JasperFx.Events.Daemon.DaemonMode.Solo);

        // services.AddHealthChecks()
        //    .AddNpgSql(configuration.GetConnectionString("DefaultConnection")!, tags: new[] { "db", "ready" });

        services.AddHttpClient("ExternalApi")
                .AddStandardResilienceHandler();

        services.AddScoped<barakoCMS.Core.Interfaces.IEmailService, barakoCMS.Infrastructure.Services.MockEmailService>();
        services.AddScoped<barakoCMS.Core.Interfaces.ISmsService, barakoCMS.Infrastructure.Services.MockSmsService>();
        services.AddScoped<barakoCMS.Core.Interfaces.ISensitivityService, barakoCMS.Infrastructure.Services.SensitivityService>();
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
        services.AddSingleton<FastEndpoints.IGlobalPostProcessor, barakoCMS.Infrastructure.Filters.SensitivityFilter>();

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

        // CORS (Must be before Authentication/Authorization)
        app.UseCors("SecurePolicy");

        app.UseAuthentication();
        
        // Token Revocation Check (Must be after Authentication)
        app.UseMiddleware<barakoCMS.Infrastructure.Middleware.TokenValidationMiddleware>();
        
        app.UseAuthorization();
        app.UseFastEndpoints(c =>
        {
            c.Errors.UseProblemDetails();
            c.Endpoints.Configurator = ep =>
            {
                ep.PreProcessors(Order.Before, new barakoCMS.Infrastructure.Filters.IdempotencyFilter());
                ep.PostProcessors(Order.Before, new barakoCMS.Infrastructure.Filters.SensitivityFilter());
            };
        });

        // Health Checks Endpoint (JSON for UI)
        app.UseHealthChecks("/health", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
        {
            Predicate = _ => true,
            ResponseWriter = HealthChecks.UI.Client.UIResponseWriter.WriteHealthCheckUIResponse
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

        app.UseCors("SecurePolicy");
        if (env == "Development")
        {
            app.UseSwaggerGen();
        }

        return app;
    }
}
