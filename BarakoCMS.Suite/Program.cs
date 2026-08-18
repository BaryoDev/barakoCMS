using Serilog;
using barakoCMS.Extensions;
using BarakoCMS.Accounting;
using BarakoCMS.Import;
using BarakoCMS.Files;
using BarakoCMS.Email.Resend;
using BarakoCMS.ExternalAuth;
using BarakoCMS.Analytics.Umami;
using BarakoCMS.Diagnostics;

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

// Outbound HTTP for the ExternalAuth OAuth token exchange + profile lookups.
builder.Services.AddHttpClient();

builder.Services.AddBarakoCMS(builder.Configuration, modules =>
{
    modules.Add(new AccountingModule());
    modules.Add(new ImportModule());
    modules.Add(new FilesModule());
    modules.Add(new BarakoCMS.Files.S3.S3FilesModule()); // dormant unless Files:S3 is configured
    modules.Add(new ResendEmailModule());
    modules.Add(new BarakoCMS.DeviceTrust.DeviceTrustModule());
    modules.Add(new ExternalAuthModule());
    modules.Add(new BarakoCMS.FeatureFlags.FeatureFlagsModule());
    modules.Add(new BarakoCMS.Portability.PortabilityModule());
    modules.Add(new UmamiAnalyticsModule());
    modules.Add(new BarakoCMS.Pwa.PwaModule());
    modules.Add(new BarakoCMS.AI.AiModule()); // dormant unless Ai:Enabled is true
    modules.Add(new DiagnosticsModule());
});

var app = builder.Build();

app.UseBarakoCMS();

// Create/patch the schema before anything reads it, so a fresh database has its tables before the
// seeders query them.
await app.ApplyMartenSchemaAsync();

if (!string.Equals(Environment.GetEnvironmentVariable("SKIP_SEEDER"), "true", StringComparison.OrdinalIgnoreCase))
{
    // Core baseline first: system roles + the InitialAdmin user. Without this a fresh Suite install
    // has no one to sign in as — the module seeders below only add module data, not an admin.
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

app.Run();

// Exposed so integration tests can boot this host if needed.
public partial class Program { }
