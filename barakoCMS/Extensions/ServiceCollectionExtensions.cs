using FastEndpoints;
using FastEndpoints.Swagger;
using FastEndpoints.Security;
using Marten;
using Marten.Events.Projections;
using Weasel.Core;
using barakoCMS.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Builder;

namespace barakoCMS.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddBarakoCMS(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddFastEndpoints();
        services.SwaggerDocument();
        services.AddHealthChecks();

        services.AddJWTBearerAuth(configuration["JWT:Key"]!);
        services.AddAuthorization();
        services.AddScoped<barakoCMS.Repository.IUserRepository, barakoCMS.Repository.MartenUserRepository>();

        services.AddMarten(sp =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            var connectionString = config.GetConnectionString("DefaultConnection");
            var options = new StoreOptions();
            options.Connection(connectionString!);

            // Configure document versioning
            options.Schema.For<Content>().DocumentAlias("contents");
            options.Schema.For<User>().DocumentAlias("users");

            options.Projections.Snapshot<Content>(SnapshotLifecycle.Inline);
            
            return options;
        }).UseLightweightSessions();

        services.AddScoped<barakoCMS.Core.Interfaces.IEmailService, barakoCMS.Infrastructure.Services.MockEmailService>();
        services.AddScoped<barakoCMS.Core.Interfaces.ISmsService, barakoCMS.Infrastructure.Services.MockSmsService>();
        services.AddScoped<barakoCMS.Core.Interfaces.ISensitivityService, barakoCMS.Infrastructure.Services.SensitivityService>();
        services.AddScoped<barakoCMS.Features.Workflows.WorkflowEngine>();

        services.AddSingleton<FastEndpoints.IGlobalPreProcessor, barakoCMS.Infrastructure.Filters.IdempotencyFilter>();
        services.AddSingleton<FastEndpoints.IGlobalPostProcessor, barakoCMS.Infrastructure.Filters.SensitivityFilter>();

        return services;
    }

    public static IApplicationBuilder UseBarakoCMS(this IApplicationBuilder app)
    {
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
        app.UseSwaggerGen();
        
        return app;
    }
}
