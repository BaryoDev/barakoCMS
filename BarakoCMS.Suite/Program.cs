using JasperFx;
using Serilog;
using barakoCMS.Extensions;

// "Barako" — the full-suite barakoCMS host: the core engine with every module on. Configure it with
// just a DATABASE_URL (or ConnectionStrings__DefaultConnection) and a 32+ char JWT__Key; every
// module's own config (Resend email, OAuth, etc.) stays optional.

// barakoCMS stores UTC DateTime values and relies on this Npgsql switch (set before Npgsql
// initializes) to bind them to 'timestamp without time zone' columns.
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

var builder = WebApplication.CreateBuilder(args);

// Before any registration, because core writes to Serilog's static Log during AddBarakoCMS and
// during module seeding. Without a logger assigned here those calls reach Serilog's silent default
// and are discarded, so a module seed failure would report to nothing.
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
});

try
{
    // Outbound HTTP for the ExternalAuth OAuth token exchange + profile lookups.
    builder.Services.AddHttpClient();

    // No module list. Discovery reads this project's dependency context and finds every referenced
    // module: the thirteen the hand-written list used to name, plus Email.Smtp, which the project
    // referenced all along and the list never added. BarakoCMS:Modules:Enabled decides which of them
    // run; unset, all of them do. Files.S3, AI and Email.Smtp stay dormant until their own section is
    // configured (Modules:Files.S3:Bucket, Modules:AI:Enabled, Modules:Email.Smtp:Host), which
    // SuiteCompositionTests holds. Adding a module is now a package reference and a restart.
    builder.Services.AddBarakoCMS(builder.Configuration);

    var app = builder.Build();

    app.UseBarakoCMS();

    // A bare first argument names a JasperFx command (db-assert, db-patch, db-apply, help), the same
    // rule the core host uses. This is the host the published image runs, so the upgrade doc and the
    // schema refusal can only name those commands if this host dispatches them (#662). A command must
    // not apply the schema first: db-assert and db-patch exist to inspect a database the apply below
    // would refuse to touch.
    var willServe = args.Length == 0 || args[0].StartsWith('-') || args[0] == "run";

    // Create/patch the schema before anything reads it, so a fresh database has its tables before the
    // seeders query them.
    if (willServe)
    {
        await app.ApplyMartenSchemaAsync();
    }

    if (willServe && !string.Equals(Environment.GetEnvironmentVariable("SKIP_SEEDER"), "true", StringComparison.OrdinalIgnoreCase))
    {
        // Core baseline first: system roles + the InitialAdmin user. Without this a fresh Suite install
        // has no one to sign in as. The module seeders below only add module data, not an admin.
        // Idempotent (seeds are guarded by existence checks).
        await barakoCMS.Data.DataSeeder.SeedAsync(app);
        // Module seeds are isolated from each other, so a failure here means one module has no baseline
        // data while the rest are fine. Logged rather than fatal: a broken module should not stop an
        // otherwise working CMS from starting, and the alternative is an image that will not boot
        // because of something optional.
        try
        {
            await app.RunBarakoModuleSeedersAsync(); // module baseline data (accounting accounts, etc.)
        }
        catch (AggregateException ex)
        {
            Log.Error(ex, "{Count} module seeder(s) failed. Those modules are running without their "
                          + "baseline data; every other module seeded normally.", ex.InnerExceptions.Count);
        }
    }

    // Runs the host exactly as app.Run() did when no command was named. A failed command comes back as
    // a return value, not an exception, and has to reach the exit code or db-assert cannot fail a deploy.
    Environment.ExitCode = await app.RunJasperFxCommands(args);
}
catch (Exception ex)
{
    // Handled rather than left to the runtime. An unhandled exception ends in abort(), and in a
    // container this process is PID 1, which the kernel does not kill with a signal it has no
    // handler for. The image logged the error and then spun instead of exiting (#763).
    Log.Fatal(ex, "Host terminated unexpectedly");
    Environment.ExitCode = 1;
}
finally
{
    Log.CloseAndFlush();
}

// Exposed so integration tests can boot this host if needed.
public partial class Program { }
