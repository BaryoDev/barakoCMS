using FastEndpoints;
using FastEndpoints.Swagger;
using FastEndpoints.Security;
using Marten;
using Marten.Events.Projections;
using Weasel.Core;
using barakoCMS.Models;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddFastEndpoints();
builder.Services.SwaggerDocument();
builder.Services.AddHealthChecks();

builder.Services.AddJWTBearerAuth(builder.Configuration["JWT:Key"]!);
builder.Services.AddAuthorization();
builder.Services.AddScoped<barakoCMS.Repository.IUserRepository, barakoCMS.Repository.MartenUserRepository>();

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");

builder.Services.AddMarten((StoreOptions options) =>
{
    options.Connection(connectionString!);
    


    // Configure document versioning
    options.Schema.For<Content>().DocumentAlias("contents");
    options.Schema.For<User>().DocumentAlias("users");

    options.Projections.Snapshot<Content>(SnapshotLifecycle.Inline);
});

var app = builder.Build();

// Configure the HTTP request pipeline.
app.UseAuthentication();
app.UseAuthorization();
app.UseFastEndpoints(c => 
{
    c.Errors.UseProblemDetails();
});
app.UseSwaggerGen();

app.MapHealthChecks("/health");

await barakoCMS.Data.DataSeeder.SeedAsync(app);

app.Run();
public partial class Program { }
