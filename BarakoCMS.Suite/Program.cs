using barakoCMS.Extensions;
using BarakoCMS.Accounting;
using BarakoCMS.Import;
using BarakoCMS.Files;
using BarakoCMS.Email.Resend;
using BarakoCMS.ExternalAuth;
using BarakoCMS.Analytics.Umami;

// "Barako" — the full-suite barakoCMS host: the core engine with every module on. Configure it with
// just a DATABASE_URL (or ConnectionStrings__DefaultConnection) and a 32+ char JWT__Key; every
// module's own config (Resend email, OAuth, etc.) stays optional.

// barakoCMS stores UTC DateTime values and relies on this Npgsql switch (set before Npgsql
// initializes) to bind them to 'timestamp without time zone' columns.
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

var builder = WebApplication.CreateBuilder(args);

// Outbound HTTP for the ExternalAuth OAuth token exchange + profile lookups.
builder.Services.AddHttpClient();

builder.Services.AddBarakoCMS(builder.Configuration, modules =>
{
    modules.Add(new AccountingModule());
    modules.Add(new ImportModule());
    modules.Add(new FilesModule());
    modules.Add(new ResendEmailModule());
    modules.Add(new BarakoCMS.DeviceTrust.DeviceTrustModule());
    modules.Add(new ExternalAuthModule());
    modules.Add(new BarakoCMS.FeatureFlags.FeatureFlagsModule());
    modules.Add(new BarakoCMS.Portability.PortabilityModule());
    modules.Add(new UmamiAnalyticsModule());
    modules.Add(new BarakoCMS.Pwa.PwaModule());
});

var app = builder.Build();

app.UseBarakoCMS();

if (!string.Equals(Environment.GetEnvironmentVariable("SKIP_SEEDER"), "true", StringComparison.OrdinalIgnoreCase))
{
    await app.RunBarakoModuleSeedersAsync(); // module baseline data (roles, etc.)
}

app.Run();

// Exposed so integration tests can boot this host if needed.
public partial class Program { }
