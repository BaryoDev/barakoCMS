using System.Threading.RateLimiting;
using barakoCMS.Modules;
using Marten;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace BarakoCMS.Forms;

/// <summary>
/// Lets a public visitor submit a form. The form is a content type: marking one with
/// <c>PUT /api/forms/{contentType}</c> makes <c>POST /api/public/forms/{contentType}</c> accept
/// anonymous submissions validated against that type's own fields.
/// </summary>
/// <remarks>
/// A submission is an ordinary content entry, created through <c>IContentWriter</c>, so a workflow on
/// Created for the type sees it like any other entry. That is where notification belongs.
/// </remarks>
public sealed class FormsModule : IBarakoModule
{
    public string Name => "Forms";

    public int HttpContractVersion => 1;

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        // `configuration` is this module's own section, Modules:Forms.
        FormsOptions.RequireValidPerForm(configuration);
        services.Configure<FormsOptions>(configuration);
        services.AddHttpClient<ITurnstileVerifier, TurnstileVerifier>();

        services.Configure<RateLimiterOptions>(options =>
            options.AddPolicy(FormsOptions.RateLimitPolicy, context =>
                Partition(context, context.RequestServices.GetRequiredService<IOptions<FormsOptions>>().Value)));
    }

    /// <summary>
    /// One bucket per client IP across every form, unless <see cref="FormsOptions.PerForm"/> names
    /// the form in the route, which then has its own bucket per client IP and its own numbers.
    /// </summary>
    /// <remarks>
    /// The limiter runs before the tenant is resolved and cannot read a form, so the form is the
    /// slug in the route matched against configuration, in any case. The bucket is keyed on the
    /// configured spelling and never on what the caller sent: a slug nobody configured, real or
    /// not, lands in the shared bucket, so sending other slugs or other spellings opens no new one.
    /// The tenant is left out of the key because <c>X-Tenant</c> and the host are the caller's to
    /// choose, and a bucket per value sent would be no limit.
    /// </remarks>
    internal static RateLimitPartition<string> Partition(HttpContext context, FormsOptions limits)
    {
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        return limits.OwnLimit(context.Request.RouteValues["slug"] as string) is { } own
            ? Window($"forms-form|{own.Form}|{ip}", own.PermitLimit, own.WindowSeconds)
            : Window($"forms-{ip}", limits.PermitLimit, limits.WindowSeconds);
    }

    private static RateLimitPartition<string> Window(string key, int permitLimit, int windowSeconds) =>
        RateLimitPartition.GetFixedWindowLimiter(key, _ =>
            new FixedWindowRateLimiterOptions
            {
                PermitLimit = Math.Max(1, permitLimit),
                Window = TimeSpan.FromSeconds(Math.Max(1, windowSeconds)),
            });

    public void ConfigureSchema(IModuleSchema schema)
    {
        // Per tenant, like the content types it points at: a form in one tenant says nothing about
        // a type of the same name in another.
        schema.For<PublicForm>()
            .DocumentAlias("public_forms")
            .Identity(x => x.ContentType);
    }

    /// <summary>
    /// Gives this module's capability to Admin, which core cannot do because it does not know the
    /// module exists. Additive and idempotent.
    /// </summary>
    public Task SeedAsync(IDocumentSession session, IServiceProvider services, CancellationToken ct) =>
        ModuleCapabilities.GrantAsync(session, FormsCapabilities.SeededRoles, FormsCapabilities.All, ct);
}
