using System.Threading.RateLimiting;
using barakoCMS.Modules;
using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;

namespace BarakoCMS.Forms;

/// <summary>
/// Somewhere for a contact form to go. Define a form under <c>/api/forms</c>, point a website's
/// form at <c>POST /api/public/forms/{name}</c>, and read what arrives under
/// <c>/api/forms/{name}/submissions</c>.
/// </summary>
/// <remarks>
/// The public endpoint is anonymous, so it is a target, and the protections are the main body of
/// the module: a honeypot field, a body cap, a per-field cap, a per-address rate limit tighter than
/// the global one, and submissions that live in their own Sensitive document rather than in
/// content. See issue #110.
/// </remarks>
public sealed class FormsModule : IBarakoModule
{
    public const string RateLimitPolicy = "forms";

    public string Name => "Forms";

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        // `configuration` is this module's own section, Modules:Forms.
        services.Configure<FormsOptions>(configuration);

        // Read once at startup rather than per request: a policy is built when the limiter first
        // sees an address, and re-reading configuration inside it would mean two addresses could
        // run under two different limits after a reload.
        var perMinute = configuration.GetValue<int?>(nameof(FormsOptions.SubmissionsPerMinute))
                        ?? FormsOptions.DefaultSubmissionsPerMinute;
        if (perMinute < 1) perMinute = FormsOptions.DefaultSubmissionsPerMinute;

        // Same shape as core's "telemetry" policy for the Diagnostics ingest, and for the same
        // reason: unauthenticated, writes a row per call, so the global 100 a minute is too loose.
        // AddRateLimiter is additive (it is a Configure<RateLimiterOptions>), so this joins the
        // policies core registered rather than replacing them.
        services.AddRateLimiter(options =>
        {
            options.AddPolicy(RateLimitPolicy, context =>
            {
                var address = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

                return RateLimitPartition.GetFixedWindowLimiter($"forms-{address}", _ =>
                    new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = perMinute,
                        Window = TimeSpan.FromMinutes(1),
                    });
            });
        });
    }

    public void ConfigureSchema(IModuleSchema schema)
    {
        schema.For<FormDefinition>()
            .MultiTenanted()
            .DocumentAlias("form_definitions")
            .Index(x => x.Name, idx =>
            {
                idx.IsUnique = true;
                // Per tenant, or the first tenant to name a form "contact" takes it from the rest.
                idx.TenancyScope = Marten.Schema.Indexing.Unique.TenancyScope.PerTenant;
            });

        schema.For<FormSubmission>()
            .MultiTenanted()
            .DocumentAlias("form_submissions")
            .Index(x => x.FormName)
            .Index(x => x.SubmittedAt);
    }

    /// <summary>
    /// Gives this module's capabilities to the Admin role.
    /// </summary>
    /// <remarks>
    /// Core cannot do this: <c>SystemCapabilities.DefaultsFor</c> does not know this module exists.
    /// Additive and idempotent, and it skips a role the host never seeded.
    /// </remarks>
    public Task SeedAsync(IDocumentSession session, IServiceProvider services, CancellationToken ct) =>
        ModuleCapabilities.GrantAsync(session, FormsCapabilities.SeededRoles, FormsCapabilities.All, ct);
}
