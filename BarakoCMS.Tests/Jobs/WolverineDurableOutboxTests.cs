using System.Collections.Concurrent;
using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Jobs;
using barakoCMS.Infrastructure.Multitenancy;
using FluentAssertions;
using JasperFx;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Xunit;

namespace BarakoCMS.Tests.Jobs;

/// <summary>A document the probe handler writes, to show which tenant's session it ran in.</summary>
public sealed class WolverineProbeDocument
{
    public Guid Id { get; set; }

    public string Tenant { get; set; } = "";
}

public sealed record WolverineProbeMessage(Guid Key);

public sealed class WolverineProbeHandler(IDocumentSession session, WolverineProbeLog log)
    : IDurableMessageHandler<WolverineProbeMessage>
{
    public async Task HandleAsync(WolverineProbeMessage message, DurableMessageContext context, CancellationToken cancellationToken)
    {
        session.Store(new WolverineProbeDocument { Id = message.Key, Tenant = context.Tenant });
        await session.SaveChangesAsync(cancellationToken);
        log.Handled.Add((message.Key, context.Tenant, context.MessageId));
        log.Seen.TrySetResult();
    }
}

public sealed class WolverineProbeLog
{
    public ConcurrentBag<(Guid Key, string Tenant, string MessageId)> Handled { get; } = new();

    public TaskCompletionSource Seen { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>A hosted service with no request, which queues a job for the tenant it was told.</summary>
public sealed class WolverineProbeQueuer(DurableTenantPublisher publisher, string tenant, Guid key) : BackgroundService
{
    public string? MessageId { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) =>
        MessageId = await publisher.EnqueueAsync(tenant, new WolverineProbeMessage(key), stoppingToken);
}

/// <summary>
/// Durable work on Wolverine behind <see cref="IDurableOutbox"/> (#965, #528). The host is built
/// here, on the fixture's database and a schema of its own, with the switch on.
/// </summary>
[Collection("Integration4")]
public class WolverineDurableOutboxTests
{
    private const string Schema = "wolverine_t965";

    private readonly IntegrationTestFixture _fixture;

    public WolverineDurableOutboxTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string NewTenant() => $"wolv-{Guid.NewGuid():N}"[..16];

    private async Task<IHost> StartHostAsync(WolverineProbeLog log, Action<IServiceCollection>? more = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddScoped<TenantContext>();
        builder.Services.AddSingleton(log);
        builder.Services.AddScoped<IDurableMessageHandler<WolverineProbeMessage>, WolverineProbeHandler>();
        more?.Invoke(builder.Services);

        builder.Services.AddMarten(_ =>
            {
                var options = new StoreOptions();
                options.Connection(_fixture.ConnectionString);
                options.DatabaseSchemaName = "wolverine_t965_docs";
                options.AutoCreateSchemaObjects = AutoCreate.CreateOrUpdate;
                options.Policies.AllDocumentsAreMultiTenanted();
                options.Schema.For<WolverineProbeDocument>();
                return options;
            })
            .BuildSessionsWith<TenantSessionFactory>(ServiceLifetime.Scoped)
            .IntegrateDurableWork(m => m.MessageStorageSchemaName = Schema);

        var host = builder.Build();
        await host.StartAsync(Ct);
        return host;
    }

    private async Task<long> EnvelopesAsync()
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync(Ct);
        await using var cmd = new NpgsqlCommand(
            $"select (select count(*) from {Schema}.wolverine_incoming_envelopes) + (select count(*) from {Schema}.wolverine_outgoing_envelopes)",
            conn);
        return (long)(await cmd.ExecuteScalarAsync(Ct))!;
    }

    [Fact]
    public async Task A_background_service_queues_a_job_for_a_non_default_tenant_and_the_handler_runs_in_that_tenants_session()
    {
        var log = new WolverineProbeLog();
        var tenant = NewTenant();
        var key = Guid.NewGuid();
        using var host = await StartHostAsync(log);

        var queuer = new WolverineProbeQueuer(host.Services.GetRequiredService<DurableTenantPublisher>(), tenant, key);
        await queuer.StartAsync(Ct);
        await queuer.ExecuteTask!;
        await log.Seen.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        log.Handled.Should().NotBeEmpty("the handler has to have run for anything below to mean something");
        var handled = log.Handled.Should().ContainSingle(h => h.Key == key).Subject;
        handled.Tenant.Should().Be(tenant);
        handled.MessageId.Should().Be(queuer.MessageId);

        var store = host.Services.GetRequiredService<IDocumentStore>();
        await using var inTenant = store.QuerySession(tenant);
        var written = await inTenant.LoadAsync<WolverineProbeDocument>(key, Ct);
        written.Should().NotBeNull("the handler wrote with a session opened for the tenant that queued the job");
        written!.Tenant.Should().Be(tenant);

        await using var inDefault = store.QuerySession();
        (await inDefault.LoadAsync<WolverineProbeDocument>(key, Ct)).Should().BeNull(
            "the default partition is not where the job was queued");
    }

    [Fact]
    public async Task A_request_that_throws_after_publishing_leaves_no_envelope_and_the_handler_never_runs()
    {
        var log = new WolverineProbeLog();
        var tenant = NewTenant();
        using var host = await StartHostAsync(log);

        var committedKey = Guid.NewGuid();
        var before = await EnvelopesAsync();

        var lostKey = Guid.NewGuid();
        var act = async () =>
        {
            using var scope = host.Services.CreateScopeForTenant(tenant);
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var outbox = scope.ServiceProvider.GetRequiredService<IDurableOutbox>();
            await outbox.EnqueueAsync(new WolverineProbeMessage(lostKey), Ct);
            throw new InvalidOperationException("the request failed before it saved");
        };

        await act.Should().ThrowAsync<InvalidOperationException>();
        (await EnvelopesAsync()).Should().Be(before, "nothing was committed, so no envelope was written");
        log.Handled.Should().BeEmpty();

        // The same flow with a save does queue and run, so the empty result above is not an idle host.
        using (var scope = host.Services.CreateScopeForTenant(tenant))
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var outbox = scope.ServiceProvider.GetRequiredService<IDurableOutbox>();
            await outbox.EnqueueAsync(new WolverineProbeMessage(committedKey), Ct);
            await session.SaveChangesAsync(Ct);
        }

        await log.Seen.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        log.Handled.Should().NotBeEmpty();
        log.Handled.Select(h => h.Key).Should().Equal(committedKey);
        log.Handled.Should().NotContain(h => h.Key == lostKey);
    }
}
