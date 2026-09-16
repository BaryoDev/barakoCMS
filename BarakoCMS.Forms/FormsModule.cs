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

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        // `configuration` is this module's own section, Modules:Forms.
        services.Configure<FormsOptions>(configuration);
        services.AddHttpClient<ITurnstileVerifier, TurnstileVerifier>();

        services.Configure<RateLimiterOptions>(options =>
            options.AddPolicy(FormsOptions.RateLimitPolicy, context =>
            {
                var limits = context.RequestServices.GetRequiredService<IOptions<FormsOptions>>().Value;
                var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

                return RateLimitPartition.GetFixedWindowLimiter($"forms-{ip}", _ =>
                    new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = Math.Max(1, limits.PermitLimit),
                        Window = TimeSpan.FromSeconds(Math.Max(1, limits.WindowSeconds)),
                    });
            }));
    }

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
