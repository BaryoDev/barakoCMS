using barakoCMS.Extensions;
using Serilog;
using Serilog.Events;
using Prometheus;

// The codebase stores UTC DateTime values throughout. Npgsql 6+ refuses to bind a Kind=UTC
// DateTime to a 'timestamp without time zone' column, which made every LINQ query that compares
// a DateTime field to DateTime.UtcNow throw — silently breaking token-revocation checks. This
// switch (set before Npgsql initializes) restores the DateTime<->timestamp mapping the code assumes.
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

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

// NOTE: /health is mapped once, inside UseBarakoCMS (see ServiceCollectionExtensions),
// with a minimal response writer so it doesn't leak internal check details to anonymous callers.

// Prometheus Metrics
app.UseHttpMetrics();
app.MapMetrics();

try
{
    Log.Information("Starting BarakoCMS Host...");

    // Run Seeder in background to avoid blocking startup and timeouts.
    if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SKIP_SEEDER")))
    {
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
    }

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
