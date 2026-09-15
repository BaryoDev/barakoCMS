using barakoCMS.Extensions;
using barakoCMS.Features.WebhookDeliveries;
using barakoCMS.Features.Workflows;
using barakoCMS.Infrastructure.Jobs;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using FastEndpoints;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// Background work reaches every tenant when Postgres enforces the tenant filter (#877).
/// </summary>
/// <remarks>
/// The shared fixture connects as the container's superuser, which row level security never binds,
/// so no test on it can see this. These run against a database of their own, owned by a
/// <c>NOSUPERUSER</c> role with <see cref="DatabaseTenancy.EnabledKey"/> on: the configuration
/// docs/tenancy-at-the-database.md describes, and the one <see cref="DatabaseTenancy.AssertUsableAsync"/>
/// lets start.
///
/// Every tenant is registered after the host was built, and each pair includes an inactive one, so
/// a registry read cached at startup or filtered on <see cref="Tenant.IsActive"/> fails here too.
/// </remarks>
[Collection("Sequential")]
public class TenantPartitionsTests
{
    private readonly IntegrationTestFixture _fixture;

    public TenantPartitionsTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static readonly SemaphoreSlim HostGate = new(1, 1);

    /// <summary>
    /// One host for the class, built on first use. Never started, so no hosted service polls the
    /// database behind a test's back: each test drives the pass it is about.
    /// </summary>
    private static WebApplication? _host;

    [Fact]
    public async Task The_enforced_database_refuses_the_cross_tenant_query_the_services_used()
    {
        // The guard on the environment rather than on the fix. If this host stopped enforcing (a
        // superuser connection, the setting lost), the tests below would pass without proving
        // anything, so this has to fail first.
        var ct = TestContext.Current.CancellationToken;
        var host = await HostAsync(ct);
        var store = host.Services.GetRequiredService<IDocumentStore>();

        var tenant = await RegisterAsync(store, "rls-guard", active: true, ct);
        await QueueRunAsync(store, tenant, ct);

        await using var conn = store.Storage.Database.CreateConnection();
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "select distinct tenant_id from public.mt_doc_workflow_runs";

        var reading = async () =>
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) { }
        };

        (await reading.Should().ThrowAsync<PostgresException>(
            "the policy reads app.tenant_id, and a bare connection never sets it"))
            .Which.SqlState.Should().Be(PostgresErrorCodes.UndefinedObject);
    }

    [Fact]
    public async Task A_run_queued_in_a_named_tenant_is_claimed_and_completed_with_enforcement_on()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = await HostAsync(ct);
        var store = host.Services.GetRequiredService<IDocumentStore>();

        var runs = new Dictionary<string, Guid>();
        foreach (var (prefix, active) in Partitions("rls-run"))
        {
            var tenant = await RegisterAsync(store, prefix, active, ct);
            runs[tenant] = await QueueRunAsync(store, tenant, ct);
        }

        var runner = new WorkflowRunner(
            host.Services, NullLogger<WorkflowRunner>.Instance, host.Services.GetRequiredService<IConfiguration>());

        var polls = 0;
        while (await runner.RunOnceAsync(ct))
        {
            (++polls).Should().BeLessThan(50, "the runner should drain rather than find work forever");
        }

        runs.Should().HaveCount(3);
        foreach (var (tenant, runId) in runs)
        {
            await using var check = store.QuerySession(tenant);
            var run = await check.LoadAsync<WorkflowRun>(runId, ct);

            run.Should().NotBeNull();
            run!.Status.Should().Be(RunStatus.Succeeded, $"the run queued in {tenant} has to execute");
            run.Actions.Should().ContainSingle().Which.Status.Should().Be(AttemptStatus.Succeeded);
        }
    }

    [Fact]
    public async Task Run_retention_removes_expired_runs_in_every_tenant_with_enforcement_on()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = await HostAsync(ct);
        var store = host.Services.GetRequiredService<IDocumentStore>();
        var now = DateTimeOffset.UtcNow;

        var kept = new Dictionary<string, Guid>();
        foreach (var (prefix, active) in Partitions("rls-runs"))
        {
            var tenant = await RegisterAsync(store, prefix, active, ct);

            await using var session = store.LightweightSession(tenant);
            session.Store(FinishedRun(now.AddDays(-400)));
            var recent = FinishedRun(now.AddDays(-1));
            session.Store(recent);
            await session.SaveChangesAsync(ct);

            kept[tenant] = recent.Id;
        }

        var service = new WorkflowRunRetentionService(
            store, host.Services.GetRequiredService<IConfiguration>(), NullLogger<WorkflowRunRetentionService>.Instance);

        (await service.TrySweepAllTenantsAsync(now, ct)).Should().BeTrue();

        kept.Should().HaveCount(3);
        foreach (var (tenant, recentId) in kept)
        {
            await using var check = store.QuerySession(tenant);
            var survivors = await check.Query<WorkflowRun>()
                .Where(r => r.WorkflowName == "Finished")
                .Select(r => r.Id)
                .ToListAsync(ct);

            survivors.Should().Equal([recentId],
                $"in {tenant} the run past its window goes and the one inside it stays");
        }
    }

    [Fact]
    public async Task Delivery_retention_removes_expired_rows_in_every_tenant_with_enforcement_on()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = await HostAsync(ct);
        var store = host.Services.GetRequiredService<IDocumentStore>();
        var now = DateTimeOffset.UtcNow;

        var kept = new Dictionary<string, Guid>();
        foreach (var (prefix, active) in Partitions("rls-deliveries"))
        {
            var tenant = await RegisterAsync(store, prefix, active, ct);

            await using var session = store.LightweightSession(tenant);
            session.Store(Delivery(now.AddDays(-400)));
            var recent = Delivery(now.AddDays(-1));
            session.Store(recent);
            await session.SaveChangesAsync(ct);

            kept[tenant] = recent.Id;
        }

        var service = new WebhookDeliveryRetentionService(
            store, host.Services.GetRequiredService<IConfiguration>(), NullLogger<WebhookDeliveryRetentionService>.Instance);

        var (removed, _) = await service.SweepAllTenantsAsync(now, ct);
        removed.Should().BeGreaterThanOrEqualTo(3);

        kept.Should().HaveCount(3);
        foreach (var (tenant, recentId) in kept)
        {
            await using var check = store.QuerySession(tenant);
            var survivors = await check.Query<WebhookDelivery>().Select(d => d.Id).ToListAsync(ct);

            survivors.Should().Equal([recentId],
                $"in {tenant} the row past its window goes and the one inside it stays");
        }
    }

    [Fact]
    public async Task A_job_queued_in_a_named_tenant_is_claimed_with_enforcement_on()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = await HostAsync(ct);
        var store = host.Services.GetRequiredService<IDocumentStore>();
        var queue = $"rls-claim-{Guid.NewGuid():N}"[..18];

        var queued = new Dictionary<string, Guid>();
        foreach (var (prefix, active) in Partitions("rls-jobs"))
        {
            var tenant = await RegisterAsync(store, prefix, active, ct);
            queued[tenant] = await QueueJobAsync(store, tenant, queue, complete: false, ct);
        }

        var claimed = await JobStorage(host).GetNextBatchAsync(SearchParams<PendingJobSearchParams<JobRecord>>(
            ("QueueID", queue),
            ("Match", (System.Linq.Expressions.Expression<Func<JobRecord, bool>>)(r => r.QueueID == queue)),
            ("Limit", 10),
            ("ExecutionTimeLimit", TimeSpan.FromMinutes(1)),
            ("CancellationToken", ct)));

        queued.Should().HaveCount(3);
        claimed.ToDictionary(r => r.TenantId, r => r.TrackingID).Should().Equal(queued,
            "a worker serves every tenant, not only the one its session defaults to");
    }

    [Fact]
    public async Task Cancel_and_purge_reach_jobs_in_a_named_tenant_with_enforcement_on()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = await HostAsync(ct);
        var store = host.Services.GetRequiredService<IDocumentStore>();
        var queue = $"rls-ops-{Guid.NewGuid():N}"[..16];

        var tenant = await RegisterAsync(store, "rls-job-ops", active: true, ct);
        var toCancel = await QueueJobAsync(store, tenant, queue, complete: false, ct);
        var toPurge = await QueueJobAsync(store, tenant, queue, complete: true, ct);

        await using (var before = store.QuerySession(tenant))
        {
            (await before.LoadAsync<JobRecord>(toPurge, ct)).Should().NotBeNull("the purge needs something to remove");
        }

        var storage = JobStorage(host);
        await storage.CancelJobAsync(toCancel, ct);
        await storage.PurgeStaleJobsAsync(SearchParams<StaleJobSearchParams<JobRecord>>(
            ("Match", (System.Linq.Expressions.Expression<Func<JobRecord, bool>>)(r => r.QueueID == queue && r.TrackingID == toPurge)),
            ("CancellationToken", ct)));

        await using var check = store.QuerySession(tenant);
        (await check.LoadAsync<JobRecord>(toCancel, ct))!.State.Should().Be(JobState.DeadLettered);
        (await check.LoadAsync<JobRecord>(toPurge, ct)).Should().BeNull("a completed job past its expiry is deleted");
    }

    /// <summary>
    /// FastEndpoints builds its search parameters itself, and gives them no public constructor or
    /// setter, so a test that calls the storage provider directly fills them the same way it does.
    /// </summary>
    private static T SearchParams<T>(params (string Name, object Value)[] values) where T : struct
    {
        object boxed = default(T);
        foreach (var (name, value) in values)
        {
            typeof(T).GetProperty(name)!.GetSetMethod(nonPublic: true)!.Invoke(boxed, [value]);
        }

        return (T)boxed;
    }

    private static MartenJobStorageProvider JobStorage(WebApplication host)
    {
        var gate = new JobStorageGate();
        gate.Open();

        return new MartenJobStorageProvider(
            host.Services.GetRequiredService<IDocumentStore>(),
            new HttpContextAccessor(),
            new JobOptions(),
            NullLogger<MartenJobStorageProvider>.Instance,
            gate,
            host.Services.GetRequiredService<IConfiguration>());
    }

    private static async Task<Guid> QueueJobAsync(
        IDocumentStore store, string tenant, string queue, bool complete, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await using var session = store.LightweightSession(tenant);
        session.Store(new JobRecord
        {
            TrackingID = id,
            TenantId = tenant,
            QueueID = queue,
            CommandType = queue,
            CommandJson = "{}",
            CreatedAt = now,
            ExecuteAfter = now.AddMinutes(-1),
            ExpireOn = complete ? now.AddMinutes(-1) : now.AddHours(4),
            DequeueAfter = now.AddMinutes(-1),
            MaxAttempts = 5,
            IsComplete = complete,
            State = complete ? JobState.Completed : JobState.Pending,
        });
        await session.SaveChangesAsync(ct);

        return id;
    }

    private async Task<WebApplication> HostAsync(CancellationToken ct)
    {
        await HostGate.WaitAsync(ct);
        try
        {
            return _host ??= await BuildEnforcedHostAsync(ct);
        }
        finally
        {
            HostGate.Release();
        }
    }

    private async Task<WebApplication> BuildEnforcedHostAsync(CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N")[..12];
        var role = "barako_rls_" + id;
        var database = "barako_rls_" + id;
        var password = Guid.NewGuid().ToString("N");

        // The shape migrations/tenancy/001-app-role.sql leaves: a NOSUPERUSER login that owns what
        // the application uses. Owning the database is the shortcut to owning every table Marten
        // then creates in it. Identifiers are generated here, and DDL takes no parameters.
        await using (var admin = new NpgsqlConnection(_fixture.ConnectionString))
        {
            await admin.OpenAsync(ct);

            await using var createRole = new NpgsqlCommand(
                $"CREATE ROLE \"{role}\" LOGIN PASSWORD '{password}' NOSUPERUSER NOCREATEDB NOCREATEROLE", admin);
            await createRole.ExecuteNonQueryAsync(ct);

            await using var createDatabase = new NpgsqlCommand($"CREATE DATABASE \"{database}\" OWNER \"{role}\"", admin);
            await createDatabase.ExecuteNonQueryAsync(ct);
        }

        var connectionString = new NpgsqlConnectionStringBuilder(_fixture.ConnectionString)
        {
            Database = database,
            Username = role,
            Password = password,
        }.ConnectionString;

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = connectionString,
            ["DATABASE_URL"] = string.Empty,
            ["JWT:Key"] = IntegrationTestFixture.JwtKey,
            ["JWT:Issuer"] = "BarakoTest",
            ["JWT:Audience"] = "BarakoClient",
            ["Connectors:Key"] = "test-connectors-key-that-is-its-own-and-long-enough",
            ["Seed:DemoContent"] = "false",
            [DatabaseTenancy.EnabledKey] = "true",
        });

        builder.Services.AddBarakoCMS(builder.Configuration, modules => modules.Discover = false);
        builder.Services.AddFastEndpoints(o =>
        {
            o.DisableAutoDiscovery = true;
            o.Assemblies = [typeof(barakoCMS.Modules.IBarakoModule).Assembly];
        });
        builder.Services.AddScoped<IWorkflowAction, CredentialEchoAction>();

        var app = builder.Build();

        // Runs DatabaseTenancy.AssertUsableAsync first, so a connection row level security does not
        // bind refuses here rather than letting every test below pass on nothing.
        await app.ApplyMartenSchemaAsync();

        return app;
    }

    /// <summary>
    /// An active tenant, an inactive one, and the default partition (a null prefix), which is where
    /// a single-tenant deployment keeps everything.
    /// </summary>
    private static (string? Prefix, bool Active)[] Partitions(string prefix) =>
        [(prefix, true), (prefix + "-inactive", false), (null, true)];

    private static async Task<string> RegisterAsync(IDocumentStore store, string? prefix, bool active, CancellationToken ct)
    {
        if (prefix is null) return JasperFx.StorageConstants.DefaultTenantId;

        var slug = $"{prefix}-{Guid.NewGuid():N}"[..(prefix.Length + 9)];

        await using var session = store.LightweightSession();
        session.Store(new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = slug, IsActive = active });
        await session.SaveChangesAsync(ct);

        return slug;
    }

    private static async Task<Guid> QueueRunAsync(IDocumentStore store, string tenant, CancellationToken ct)
    {
        await using var session = store.LightweightSession(tenant);

        var content = new Content
        {
            Id = Guid.NewGuid(),
            ContentType = "rls-probe",
            Status = ContentStatus.Published,
            Data = new Dictionary<string, object>(),
        };

        var run = new WorkflowRun
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = Guid.NewGuid(),
            WorkflowName = "Echo",
            ContentId = content.Id,
            ContentType = content.ContentType,
            TriggerEvent = "Published",
        };

        run.Actions.Add(new WorkflowActionAttempt
        {
            Ordinal = 0,
            ActionType = "CredentialEcho",
            IdempotencyKey = $"{run.Id:N}-0",
        });
        run.Recompute();

        session.Store(content);
        session.Store(run);
        await session.SaveChangesAsync(ct);

        return run.Id;
    }

    private static WorkflowRun FinishedRun(DateTimeOffset completedAt) => new()
    {
        Id = Guid.NewGuid(),
        WorkflowDefinitionId = Guid.NewGuid(),
        WorkflowName = "Finished",
        ContentId = Guid.NewGuid(),
        ContentType = "rls-probe",
        TriggerEvent = "Published",
        Status = RunStatus.Succeeded,
        CreatedAt = completedAt,
        CompletedAt = completedAt,
    };

    private static WebhookDelivery Delivery(DateTimeOffset createdAt) => new()
    {
        Id = Guid.NewGuid(),
        WorkflowId = Guid.NewGuid(),
        Url = "https://hooks.example.com/rls",
        Event = "Published",
        CreatedAt = createdAt,
    };
}
