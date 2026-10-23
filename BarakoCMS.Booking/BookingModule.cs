using System.Threading.RateLimiting;
using barakoCMS.Core.Interfaces;
using barakoCMS.Modules;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace BarakoCMS.Booking;

/// <summary>
/// Bookable resources and bookings that cannot overlap. Enable it with:
/// <code>services.AddBarakoCMS(config, m =&gt; m.Add(new BookingModule()));</code>
/// </summary>
/// <remarks>
/// A resource is this module's own document. A booking is an ordinary content entry of the type
/// <c>Modules:Booking:ContentType</c>, so it is queried, permissioned and delivered like any other.
/// The module adds the rule on write and the public route that answers when a resource is free. It
/// seeds nothing: creating the booking type on boot would make a later blueprint apply fail with 409.
/// </remarks>
public sealed class BookingModule : IBarakoModule
{
    public string Name => "Booking";

    public int ContractVersion => ModuleContract.Version;

    public int HttpContractVersion => BookingContract.Version;

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<BookingOptions>()
            .Bind(configuration)
            .Validate(o => !string.IsNullOrWhiteSpace(o.ContentType)
                           && !string.IsNullOrWhiteSpace(o.ResourceField)
                           && !string.IsNullOrWhiteSpace(o.StartField)
                           && !string.IsNullOrWhiteSpace(o.EndField)
                           && !string.IsNullOrWhiteSpace(o.StatusField),
                "Modules:Booking type and field names may not be empty.")
            .Validate(o => o.MaxRangeDays is >= 1 and <= 366, "Modules:Booking:MaxRangeDays must be 1 to 366.")
            .Validate(o => o.MaxSlots is >= 1 and <= 10000, "Modules:Booking:MaxSlots must be 1 to 10000.")
            .Validate(o => o.PermitLimit >= 1 && o.WindowSeconds >= 1, "Modules:Booking rate limit values must be at least 1.")
            .ValidateOnStart();

        services.AddScoped<IContentLifecycleHook, BookingOverlapHook>();

        services.Configure<RateLimiterOptions>(options =>
            options.AddPolicy(BookingOptions.RateLimitPolicy, context =>
            {
                var limits = context.RequestServices.GetRequiredService<IOptions<BookingOptions>>().Value;
                var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                return RateLimitPartition.GetFixedWindowLimiter($"booking-availability-{ip}", _ =>
                    new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = limits.PermitLimit,
                        Window = TimeSpan.FromSeconds(limits.WindowSeconds),
                    });
            }));
    }

    /// <summary>Per tenant. Listed by name within a tenant's few rows, so no index.</summary>
    public void ConfigureSchema(IModuleSchema schema) =>
        schema.For<BookingResource>().DocumentAlias("booking_resources");

    public Task SeedAsync(IModuleSeedContext context, CancellationToken ct) =>
        BookingCapabilities.Defaults.GrantAsync(context, ct);

    [Obsolete("Override SeedAsync(IModuleSeedContext, CancellationToken), which names no Marten type. "
              + "Removal planned for barakoCMS 6.0.0.")]
    public Task SeedAsync(Marten.IDocumentSession session, IServiceProvider services, CancellationToken ct)
    {
#pragma warning disable CS0618
        return SeedAsync(ModuleSeedContexts.Over(session, services), ct);
#pragma warning restore CS0618
    }
}
