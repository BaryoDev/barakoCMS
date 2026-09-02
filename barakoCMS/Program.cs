using barakoCMS.Extensions;
using JasperFx;
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

// NOTE: /health, /health/live and /health/ready are mapped inside UseBarakoCMS (see
// ServiceCollectionExtensions), each with a minimal response writer so they don't leak internal
// check details to anonymous callers.

// Prometheus Metrics
app.UseHttpMetrics();
app.MapMetrics();

try
{
    Log.Information("Starting BarakoCMS Host...");

    // A bare first argument names a JasperFx command (db-assert, db-patch, db-apply, help). A
    // leading dash is a .NET or ASP.NET flag such as --urls, which JasperFx detects and hands
    // straight back to the normal host, so those still serve and still need the schema work
    // below. Deciding this on args.Length alone left a --urls host with whatever tables its first
    // request happened to create.
    var willServe = args.Length == 0 || args[0].StartsWith('-') || args[0] == "run";

    if (willServe)
    {
        // Create the schema up front, before anything reads it. Production runs
        // AutoCreate.CreateOnly, which creates missing objects but never alters an existing one, so
        // a fresh database works and an upgrade needing an ALTER fails here loudly. That is
        // deliberate: the ALTER goes through `db-patch` as a reviewed SQL file applied before the
        // deploy. See docs/upgrading-to-4.0.md.
        await app.ApplyMartenSchemaAsync();
    }

    // The seed stays in the background so the process can answer probes while it runs, but
    // readiness is held closed until it finishes. Before this, the host was ready the moment
    // Kestrel bound and then slept five seconds before seeding, so every request in that window saw
    // no roles and no admin: sign-in failed, and a registration was stored with an empty RoleIds.
    // /health and /health/live keep answering throughout, so a slow seed cannot get the pod killed.
    // See issue #256.
    if (willServe && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SKIP_SEEDER")))
    {
        var seedGate = app.Services.GetRequiredService<barakoCMS.Infrastructure.Health.StartupSeedGate>();
        seedGate.MarkPending();

        _ = Task.Run(async () =>
        {
            try
            {
                Log.Information("[Background] Starting Data Seeder...");
                await barakoCMS.Data.DataSeeder.SeedAsync(app);
                seedGate.MarkCompleted();
                Log.Information("[Background] Data Seeder Completed.");
            }
            catch (Exception ex)
            {
                seedGate.MarkFailed(ex);
                Log.Error(ex, "[Background] Data Seeder Failed!");
            }
        });
    }

    Log.Information("BarakoCMS App Running...");

    // Dispatches a db-* command when one was named, and runs the host exactly as app.Run() did
    // otherwise. A failed command comes back as a return value rather than an exception, so it has
    // to reach the exit code the same way the catch below does.
    Environment.ExitCode = await app.RunJasperFxCommands(args);
}
catch (Exception ex)
{
    Log.Fatal(ex, "Host terminated unexpectedly");
    // Everything that decides whether a deploy worked reads the exit code: CI, docker run wrappers,
    // systemd, a k8s Job container. Ending normally after a fatal error reports the broken deploy
    // as a success.
    Environment.ExitCode = 1;
}
finally
{
    Log.Information("BarakoCMS Host Shutting Down...");
    Log.CloseAndFlush();
}
// Stays public. WebApplicationFactory<Program> is the test host's entry point, and a public test
// class cannot implement IClassFixture over an internal one, so internalising this would make forty
// test classes internal to buy nothing: the type is an empty partial with no members for section 6
// to freeze.
public partial class Program { }
