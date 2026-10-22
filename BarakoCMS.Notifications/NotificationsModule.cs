using barakoCMS.Modules;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BarakoCMS.Notifications;

/// <summary>
/// Notifications for barakoCMS. Any module resolves <see cref="INotificationService"/> and raises a
/// notification for a user; it lands in that user's inbox at <c>GET /api/notifications</c> and is
/// handed to every registered <see cref="INotificationSink"/>.
/// </summary>
/// <remarks>
/// A user sees and marks read only their own. Raising over HTTP and reading everyone's need
/// <c>manage_notifications</c>.
/// </remarks>
public sealed class NotificationsModule : IBarakoModule
{
    public string Name => "Notifications";

    public int HttpContractVersion => 1;

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        // `configuration` is this module's own section, Modules:Notifications.
        services.Configure<NotificationsOptions>(configuration);
        services.TryAddScoped<INotificationService, NotificationService>();
        services.AddScoped<INotificationSink, EmailNotificationSink>();
    }

    public void ConfigureSchema(IModuleSchema schema)
    {
        schema.For<Notification>().DocumentAlias("notifications");
    }

    public Task SeedAsync(IModuleSeedContext context, CancellationToken ct) =>
        NotificationsCapabilities.Defaults.GrantAsync(context, ct);

    [Obsolete("Override SeedAsync(IModuleSeedContext, CancellationToken), which names no Marten type. "
              + "Removal planned for barakoCMS 6.0.0.")]
    public Task SeedAsync(Marten.IDocumentSession session, IServiceProvider services, CancellationToken ct)
    {
#pragma warning disable CS0618
        return SeedAsync(ModuleSeedContexts.Over(session, services), ct);
#pragma warning restore CS0618
    }
}
