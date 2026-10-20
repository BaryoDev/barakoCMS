using System.Threading.RateLimiting;
using barakoCMS.Modules;
using Microsoft.AspNetCore.RateLimiting;

namespace BarakoCMS.Payments;

/// <summary>
/// Takes payment for an entry. A content type is declared payable with
/// <c>PUT /api/payments/payable/{contentType}</c>; <c>POST /api/public/payments/checkout</c> then
/// starts a payment for an entry at the amount stored on it; the provider's webhook settles it.
/// </summary>
/// <remarks>
/// A payment is confirmed by a verified webhook and by nothing else, never by the browser coming
/// back to a success page. Provider adapters implement <see cref="IPaymentProvider"/> and are
/// registered in the container; this module ships none of its own.
/// </remarks>
public sealed class PaymentsModule : IBarakoModule
{
    public const string CheckoutRateLimitPolicy = "payments-checkout";

    public string Name => "Payments";

    public int HttpContractVersion => 1;

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<RateLimiterOptions>(options =>
            options.AddPolicy(CheckoutRateLimitPolicy, context =>
            {
                var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                return RateLimitPartition.GetFixedWindowLimiter($"payments-checkout-{ip}", _ =>
                    new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1) });
            }));
    }

    public void ConfigureSchema(IModuleSchema schema)
    {
        schema.For<PayableType>()
            .DocumentAlias("payments_payable_types")
            .Identity(x => x.ContentType);

        schema.For<PaymentAttempt>()
            .DocumentAlias("payments_attempts")
            .Index(x => x.Reference)
            .Index(x => x.EntryId)
            .Index(x => x.Status);

        schema.For<PaymentEventRecord>().DocumentAlias("payments_events");
    }

    public Task SeedAsync(IModuleSeedContext context, CancellationToken ct) =>
        PaymentsCapabilities.Defaults.GrantAsync(context, ct);

    [Obsolete("Override SeedAsync(IModuleSeedContext, CancellationToken), which names no Marten type. "
              + "Removal planned for barakoCMS 6.0.0.")]
    public Task SeedAsync(Marten.IDocumentSession session, IServiceProvider services, CancellationToken ct)
    {
#pragma warning disable CS0618
        return SeedAsync(ModuleSeedContexts.Over(session, services), ct);
#pragma warning restore CS0618
    }
}
