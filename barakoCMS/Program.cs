using barakoCMS.Extensions;
using Serilog;
using Serilog.Events;

var builder = WebApplication.CreateBuilder(args);

// 1. Configure Serilog
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateLogger();

builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.Conditional(
        evt => context.HostingEnvironment.IsProduction(), // Only file log in Prod
        wt => wt.File(
            new Serilog.Formatting.Compact.CompactJsonFormatter(),
            "logs/barako-log-.json",
            rollingInterval: RollingInterval.Day)
    ));

// Add services to the container.
builder.Services.AddBarakoCMS(builder.Configuration);

var app = builder.Build();

// Configure the HTTP request pipeline.
app.UseBarakoCMS();

app.MapHealthChecks("/health");

try
{
    Log.Information("Starting BarakoCMS Host...");
    await barakoCMS.Data.DataSeeder.SeedAsync(app);
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Host terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
public partial class Program { }
