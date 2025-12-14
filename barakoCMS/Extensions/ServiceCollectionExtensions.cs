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
        services.AddHealthChecks();

        services.AddJWTBearerAuth(configuration["JWT:Key"]!);
        services.AddAuthorization();
        services.AddCors(options =>
        {
            options.AddPolicy("SecurePolicy", builder =>
            {
                builder.WithOrigins("http://localhost:3000", "https://localhost:7049") // Adjust as needed
                       .AllowAnyMethod()
                       .AllowAnyHeader();
            });
        });
        // Repository registration
        services.AddScoped<IUserRepository, MartenUserRepository>();

        // RBAC Services
        services.AddScoped<IConditionEvaluator, ConditionEvaluator>();
        services.AddScoped<IPermissionResolver, PermissionResolver>();

        services.AddMarten((IServiceProvider sp) =>
        {
            // var config = sp.GetRequiredService<IConfiguration>();
            var connectionString = configuration.GetConnectionString("DefaultConnection");
            var options = new StoreOptions();
            options.Connection(connectionString!);

            // Configure document versioning
            options.Schema.For<Content>().DocumentAlias("contents");
            options.Schema.For<User>().DocumentAlias("users");

            // Database Automation: Schema Migration Policy
            var env = sp.GetRequiredService<IWebHostEnvironment>();
            if (env.IsDevelopment())
            {
                // In Dev: Allow destructive changes for rapid iteration
                options.AutoCreateSchemaObjects = AutoCreate.All;
            }
            else
            {
                // In Prod: Only allow safe additive changes (CreateOrUpdate)
                // Prevents accidental data loss from destructive schema changes
                options.AutoCreateSchemaObjects = AutoCreate.CreateOrUpdate;
            }

            // TODO: Fix Marten 8 Enum namespaces for SnapshotLifecycle
            // options.Projections.Snapshot<Content>(Marten.SnapshotLifecycle.Inline);

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

            // Stricter limit for authentication endpoints
            options.AddPolicy("auth", context =>
            {
                var ipAddress = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

                return RateLimitPartition.GetFixedWindowLimiter($"auth-{ipAddress}", _ =>
                    new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 10,  // Only 10 login attempts per minute
                        Window = TimeSpan.FromMinutes(1)
                    });
            });

            options.OnRejected = async (context, cancellationToken) =>
            {
                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await context.HttpContext.Response.WriteAsync(
                    "Too many requests. Please try again later.", cancellationToken);
            };
        });

        return services;
    }

    public static IApplicationBuilder UseBarakoCMS(this IApplicationBuilder app)
    {
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
                "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'");

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

        app.UseAuthentication();
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

        app.UseHealthChecks("/health");

        app.UseCors("SecurePolicy");
        if (env == "Development")
        {
            app.UseSwaggerGen();
        }

        return app;
    }
}
