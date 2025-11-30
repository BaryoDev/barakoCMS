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

        var connectionString = configuration.GetConnectionString("DefaultConnection");

        services.AddMarten((StoreOptions options) =>
        {
            options.Connection(connectionString!);
            


            // Configure document versioning
            options.Schema.For<Content>().DocumentAlias("contents");
            options.Schema.For<User>().DocumentAlias("users");

            options.Projections.Snapshot<Content>(SnapshotLifecycle.Inline);
        });

        return services;
    }

    public static IApplicationBuilder UseBarakoCMS(this IApplicationBuilder app)
    {
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseFastEndpoints(c => 
        {
            c.Errors.UseProblemDetails();
        });
        app.UseSwaggerGen();
        
        return app;
    }
}
