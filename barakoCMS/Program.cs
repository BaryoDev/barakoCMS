using barakoCMS.Extensions;
using Serilog;
using Serilog.Events;
using HealthChecks.UI.Client;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Prometheus;

var builder = WebApplication.CreateBuilder(args);

// 1. Configure Serilog
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateLogger();

builder.Host.UseSerilog((context, services, configuration) =>
{
    configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .WriteTo.Console();

    if (context.Configuration.GetValue<bool>("Serilog:WriteToFile"))
    {
        configuration.WriteTo.File("logs/log-.txt", rollingInterval: RollingInterval.Day);
    }
});

// Add services to the container.
builder.Services.AddBarakoCMS(builder.Configuration);

var app = builder.Build();

// Configure the HTTP request pipeline.
try
{
    app.UseBarakoCMS();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Failed to start BarakoCMS Pipeline!");
    Console.WriteLine(ex.ToString());
    throw;
}

app.MapHealthChecks("/health", new HealthCheckOptions
{
    ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse
});

// Prometheus Metrics
app.UseHttpMetrics();
app.MapMetrics();

try
{
    Log.Information("Starting BarakoCMS Host...");

    // DEBUG: Print Env Vars
    var dbUrl = Environment.GetEnvironmentVariable("DATABASE_URL");
    Log.Information("DEBUG: DATABASE_URL available: {Available}, Length: {Length}", !string.IsNullOrEmpty(dbUrl), dbUrl?.Length ?? 0);

    // Run Seeder in background to avoid blocking startup and timeouts.
    _ = Task.Run(async () =>
    {
        try
        {
            await Task.Delay(5000); // Wait 5s for app to warm up
            Log.Information("[Background] Starting Data Seeder...");
            using (var scope = app.Services.CreateScope())
            {
                await barakoCMS.Data.DataSeeder.SeedAsync(app);
            }
            Log.Information("[Background] Data Seeder Completed.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Background] Data Seeder Failed!");
        }
    });

    Log.Information("BarakoCMS App Running...");
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Host terminated unexpectedly");
}
finally
{
    Log.Information("BarakoCMS Host Shutting Down...");
    Log.CloseAndFlush();
}
public partial class Program { }
