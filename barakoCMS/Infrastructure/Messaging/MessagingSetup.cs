using JasperFx.Core;
using Marten;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.ErrorHandling;

namespace barakoCMS.Infrastructure.Messaging;

/// <summary>
/// Wolverine underneath the endpoints we already have (#687). The endpoints stay FastEndpoints;
/// Wolverine owns the outbox, the retry policy, the dead letter queue and the durable local queue
/// that the hand-rolled job queue owns today.
/// </summary>
/// <remarks>
/// Spike scope. Nothing here is wired into a production path yet: the handlers in
/// <see cref="Messages"/> exist so the six questions in #687 can be answered by tests.
/// </remarks>
internal static class MessagingSetup
{
    public const string SoloKey = "Messaging:Solo";
    public const string MaxAttemptsKey = "Messaging:MaxAttempts";
    public const string BackoffBaseSecondsKey = "Messaging:BackoffBaseSeconds";
    public const string BackoffMaxSecondsKey = "Messaging:BackoffMaxSeconds";

    /// <summary>Every Wolverine table lives here, so the 3.x upgrade story stays about our own schema.</summary>
    public const string SchemaName = "wolverine";

    public static IServiceCollection AddBarakoMessaging(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<MessageRecorder>();

        services.AddWolverine(opts =>
        {
            opts.ServiceName = "barakoCMS";
            opts.ApplicationAssembly = typeof(MessagingSetup).Assembly;

            // Nothing is discovered by naming convention. The core has public classes whose names end
            // in Handler that have nothing to do with Wolverine, and a spike should not find out at
            // runtime which of them it decided were message handlers.
            opts.Discovery.DisableConventionalDiscovery();
            opts.Discovery.IncludeType(typeof(LogMessageHandler));
            opts.Discovery.IncludeType(typeof(AlwaysFailsHandler));
            opts.Discovery.IncludeType(typeof(DeliverWebhookHandler));

            // A handler that takes IDocumentSession is a transaction; one that takes IQuerySession is
            // not. That is the rule MartenJobStorageProvider enforces by hand through
            // IHttpContextAccessor.
            opts.Policies.AutoApplyTransactions();

            // A message queued locally survives a restart, because it is in the inbox table before
            // the handler runs. Without this a local queue is in memory and a crash loses it, which
            // is the property the FastEndpoints job queue exists to provide.
            opts.Policies.UseDurableLocalQueues();
        });

        // Settings are read when the runtime starts, not here. Under WebApplicationFactory the
        // settings a test host adds arrive after AddBarakoCMS has run, the same reason JobOptions is
        // built from a service factory rather than from the configuration handed in here.
        services.AddWolverineExtension<MessagingSettings>();

        return services;
    }
}

/// <summary>Durability mode and the retry policy, from configuration at runtime start.</summary>
internal sealed class MessagingSettings(IConfiguration configuration) : IWolverineExtension
{
    public void Configure(WolverineOptions opts)
    {
        // The test suite keeps a hundred hosts alive on one database. Balanced mode gives each of
        // them node agents, leader election and a health-check heartbeat against the same tables;
        // Solo says "this process is alone" and skips all of it. Production stays Balanced, which is
        // Wolverine's own leader election, the thing #856 wants instead of our advisory locks.
        opts.Durability.Mode = configuration.GetValue(MessagingSetup.SoloKey, false)
            ? DurabilityMode.Solo
            : DurabilityMode.Balanced;

        // The same shape as JobOptions: MaxAttempts tries, exponential backoff with a cap, then the
        // dead letter table. Wolverine wants the cooldowns spelled out per attempt rather than a
        // base and a cap, so JobBackoff.DelayFor is reused to produce them.
        var maxAttempts = Math.Max(1, configuration.GetValue(MessagingSetup.MaxAttemptsKey, Jobs.JobOptions.DefaultMaxAttempts));
        var baseSeconds = configuration.GetValue(MessagingSetup.BackoffBaseSecondsKey, Jobs.JobOptions.DefaultBackoffBaseSeconds);
        var maxSeconds = configuration.GetValue(MessagingSetup.BackoffMaxSecondsKey, Jobs.JobOptions.DefaultBackoffMaxSeconds);
        var cooldowns = Enumerable.Range(1, maxAttempts - 1)
            .Select(attempt => Jobs.JobBackoff.DelayFor(attempt, baseSeconds, maxSeconds))
            .ToArray();

        if (cooldowns.Length > 0)
        {
            opts.OnException<Exception>().RetryWithCooldown(cooldowns).Then.MoveToErrorQueue();
        }
        else
        {
            opts.OnException<Exception>().MoveToErrorQueue();
        }
    }
}

/// <summary>
/// The session factory Wolverine builds handler sessions from.
/// </summary>
/// <remarks>
/// Wolverine's <c>OutboxedSessionFactory</c> is a singleton that takes Marten's
/// <c>ISessionFactory</c>, and ours (<see cref="Multitenancy.TenantSessionFactory"/>) is scoped
/// because it reads the request's tenant. The container refuses that pair at validation. So
/// Wolverine gets a factory of its own: a <see cref="Marten.SessionFactoryBase"/>, which
/// is the shape Wolverine prefers, since it then opens the session with the envelope's tenant id
/// on the options rather than calling back into a factory that has no request to read a tenant
/// from. The request-scoped factory is untouched: endpoints keep getting tenant-bound sessions,
/// and handlers get the envelope's tenant (#687, questions 5 and 6).
/// </remarks>
internal sealed class WolverineSessionFactory(IDocumentStore store) : Marten.SessionFactoryBase(store)
{
    public override Marten.Services.SessionOptions BuildOptions() => new() { Tracking = DocumentTracking.None };
}

internal static class WolverineSessionFactoryRegistration
{
    /// <summary>Call after <c>IntegrateWithWolverine</c>; it replaces the registration that one adds.</summary>
    public static IServiceCollection UseSingletonSessionFactoryForWolverine(this IServiceCollection services)
    {
        services.RemoveAll<Wolverine.Marten.Publishing.OutboxedSessionFactory>();
        services.AddSingleton(sp =>
        {
            var store = sp.GetRequiredService<IDocumentStore>();
            return new Wolverine.Marten.Publishing.OutboxedSessionFactory(
                new WolverineSessionFactory(store), sp.GetRequiredService<Wolverine.Runtime.IWolverineRuntime>(), store);
        });
        return services;
    }
}
