using FastEndpoints;
using static BarakoCMS.Tests.ModuleSchemaTestHelper;
using barakoCMS.Modules;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;
using Xunit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Marten;
using barakoCMS.Extensions;
using Microsoft.Extensions.Configuration;

namespace BarakoCMS.Tests;

public class IntegrationTestFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgresContainer = new PostgreSqlBuilder()
        .WithImage("pgvector/pgvector:pg16")
        .WithDatabase("barako_test_db")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .WithPassword("postgres")
        // Every class in the Sequential collection shares this container, and every host a test
        // derives with WithWebHostBuilder stays alive until another class derives one (see
        // ReleaseDerivedHostsExcept).
        // Each one runs the projection daemon and the workflow runner, polling on unpooled
        // connections, so peak connections grow with the number of live hosts. The Postgres default
        // of 100 ran out at 72 and 94 hosts on CI, failing whichever host was starting with 53300.
        // 500 covers the whole suite's ~112 hosts at 4 connections each.
        .WithCommand("-c", "max_connections=500")
        .Build();

    /// <summary>
    /// The key this host signs and validates with. FastEndpoints validates through one process-wide
    /// key, overwritten by whichever host starts last, so any other host started in this test
    /// process (a <c>BarakoTestHost</c>, say) has to use the same one.
    /// </summary>
    public const string JwtKey = "test-super-secret-key-that-is-at-least-32-chars-long";

    public IntegrationTestFixture()
    {
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");
        Environment.SetEnvironmentVariable("JWT__Key", JwtKey);
        _fallbacks = FastEndpointsResolver.RegisterFallbacks(() => _startedHosts.Reverse());
    }

    private readonly IDisposable _fallbacks;

    /// <summary>The API key this host is configured with, standing in for a deployment's own.</summary>
    public const string ConfiguredResendKey = "re_from_the_deployment_not_the_admin";

    private int _hostsBuilt;

    private readonly System.Collections.Concurrent.ConcurrentQueue<IServiceProvider> _startedHosts = new();

    /// <summary>The owner of a host that belongs to the run rather than to one test class.</summary>
    public const string RunOwner = "";

    private readonly Lock _derivedGate = new();
    private readonly List<DerivedHost> _derived = [];

    /// <summary>Resolvable from a derived host's provider, so teardown knows which class built it.</summary>
    private sealed record DerivedHostOwner(string Owner);

    /// <summary>
    /// A host derived from the fixture that fails clearly once released, instead of handing out a
    /// disposed provider that throws <see cref="ObjectDisposedException"/> deep inside whatever test
    /// cached it.
    /// </summary>
    /// <remarks>
    /// Built the way <c>WebApplicationFactory.WithWebHostBuilder</c> builds one, the fixture's own
    /// configuration and then the caller's, but as a type this file owns so <see cref="Services"/>
    /// and every client can check whether the host was released.
    /// </remarks>
    private sealed class DerivedHost(IntegrationTestFixture root, string owner, Action<IWebHostBuilder> configuration)
        : WebApplicationFactory<Program>
    {
        private string? _released;

        public string Owner => owner;

        /// <summary>Marks the host released. <paramref name="when"/> finishes "disposed ...".</summary>
        public void MarkReleased(string when) => _released = when;

        public override IServiceProvider Services
        {
            get
            {
                ThrowIfReleased();
                return base.Services;
            }
        }

        // The first step of starting a server, so a released host that never started fails here
        // rather than starting a new one before ConfigureClient gets a chance to refuse it.
        protected override Microsoft.Extensions.Hosting.IHostBuilder? CreateHostBuilder()
        {
            ThrowIfReleased();
            return base.CreateHostBuilder();
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            root.ConfigureWebHost(builder);
            configuration(builder);
            builder.ConfigureServices(services => services.AddSingleton(new DerivedHostOwner(owner)));
        }

        protected override void ConfigureClient(HttpClient client)
        {
            ThrowIfReleased();
            base.ConfigureClient(client);
        }

        private void ThrowIfReleased()
        {
            if (_released is { } when)
            {
                throw new InvalidOperationException(
                    $"IntegrationTestFixture: this host was derived by {owner} and disposed {when} "
                    + "(#1171). Something kept it past its class, most likely a cache shared by more than "
                    + "one test class. Build a shared host with WithRunScopedHost or WithRunScopedSettings.");
            }
        }
    }

    private static string CurrentClass() =>
        TestContext.Current.TestClass?.TestClassName
        ?? throw new InvalidOperationException(
            "IntegrationTestFixture: a derived host was requested outside a test class, so no class can own it. "
            + "Use WithRunScopedHost for a host that should live for the whole run.");

    /// <summary>
    /// A host derived from this one. It belongs to the test class that asked for it and is disposed
    /// when a different class derives its first host, or when this fixture is disposed (#1171).
    /// </summary>
    /// <remarks>
    /// The Sequential collection runs one class at a time, so by the time another class derives a
    /// host the earlier class has finished with its own. A static host cache only its own class
    /// reaches is therefore safe, and one several classes reach is not: such a host asks for
    /// <see cref="WithRunScopedHost"/> instead. A released host handed out again throws, naming
    /// the class that built it.
    /// </remarks>
    public new WebApplicationFactory<Program> WithWebHostBuilder(Action<IWebHostBuilder> configuration) =>
        Derive(CurrentClass(), configuration);

    /// <summary>
    /// A derived host that outlives the class that built it, for a cache several classes share.
    /// Disposed with this fixture.
    /// </summary>
    public WebApplicationFactory<Program> WithRunScopedHost(Action<IWebHostBuilder> configuration) =>
        Derive(RunOwner, configuration);

    private WebApplicationFactory<Program> Derive(string owner, Action<IWebHostBuilder> configuration)
    {
        if (owner != RunOwner)
        {
            ReleaseDerivedHostsExcept(owner);
        }

        var derived = new DerivedHost(this, owner, configuration);
        lock (_derivedGate)
        {
            _derived.Add(derived);
        }

        return derived;
    }

    /// <summary>Every derived host not yet disposed, for a diagnostic that looks across hosts.</summary>
    internal IReadOnlyList<WebApplicationFactory<Program>> LiveDerivedHosts()
    {
        lock (_derivedGate)
        {
            return [.. _derived];
        }
    }

    /// <summary>Derived hosts built by <paramref name="owner"/> and not yet disposed.</summary>
    internal int LiveDerivedHosts(string owner)
    {
        lock (_derivedGate)
        {
            return _derived.Count(d => d.Owner == owner);
        }
    }

    /// <summary>
    /// Disposes every derived host that a class other than <paramref name="owner"/> built, then puts
    /// FastEndpoints' static resolver back on a host that is still up.
    /// </summary>
    /// <remarks>
    /// <c>MapFastEndpoints</c> sets a static <c>ServiceResolver.Instance</c> to the provider of each
    /// host that maps endpoints, so the last host started owns it. Disposing that host leaves the
    /// static on a disposed provider, and <c>JwtBearer.CreateToken</c> and every other lookup made
    /// outside a request then throws <see cref="ObjectDisposedException"/>. That is the reason these
    /// hosts were never disposed. <see cref="FastEndpointsResolver.RepairIfDisposed"/> moves the
    /// static to the newest host still up (this fixture's own at the latest), which is what it
    /// pointed at before the disposed host started.
    /// </remarks>
    /// <returns>How many factories were disposed.</returns>
    internal int ReleaseDerivedHostsExcept(string owner)
    {
        List<DerivedHost> doomed;
        HashSet<string> owners;
        lock (_derivedGate)
        {
            doomed = _derived.Where(d => d.Owner != owner && d.Owner != RunOwner).ToList();
            owners = doomed.Select(d => d.Owner).ToHashSet();
            _derived.RemoveAll(doomed.Contains);
        }

        if (doomed.Count > 0)
        {
            var providers = _startedHosts.Where(sp => OwnerOf(sp) is { } o && owners.Contains(o)).ToList();
            try
            {
                StopProjectionCoordinatorsAsync(providers).GetAwaiter().GetResult();
            }
            catch (TimeoutException e)
            {
                Console.Error.WriteLine(
                    "IntegrationTestFixture: a projection coordinator did not stop in time while releasing derived "
                    + $"hosts (#1171). Disposal carries on.{Environment.NewLine}{e}");
            }

        }

        var failures = new List<Exception>();
        FastEndpointsResolver.Exclusively(() =>
        {
            failures.AddRange(DisposeEach(doomed, $"when {owner} derived its first host"));
            FastEndpointsResolver.RepairIfDisposed(_startedHosts.Reverse());
        });

        if (failures.Count > 0)
        {
            throw new AggregateException(
                $"IntegrationTestFixture: {failures.Count} derived host(s) failed to dispose (#1171).", failures);
        }

        return doomed.Count;
    }

    /// <summary>
    /// Marks and disposes each host, carrying on past one that throws so the rest still stop.
    /// </summary>
    private static List<Exception> DisposeEach(IEnumerable<DerivedHost> hosts, string when)
    {
        var failures = new List<Exception>();
        foreach (var host in hosts)
        {
            host.MarkReleased(when);
            try
            {
                host.Dispose();
            }
            catch (Exception e)
            {
                failures.Add(e);
            }
        }

        return failures;
    }

    private static string? OwnerOf(IServiceProvider services)
    {
        try
        {
            return services.GetService<DerivedHostOwner>()?.Owner;
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
    }

    public string ConnectionString => _postgresContainer.GetConnectionString().Replace("localhost", "127.0.0.1").Replace("Host=", "Server=") + ";Pooling=false";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((ctx, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "ConnectionStrings:DefaultConnection", ConnectionString },
                { "JWT:Key", JwtKey },
                { "JWT:Issuer", "BarakoTest" },
                { "JWT:Audience", "BarakoClient" },
                // The OpenAPI document is a shipped artifact (tags group a generated client, and the
                // delivery paths are generated per tenant), so tests assert against it. Explicit
                // rather than relying on ASPNETCORE_ENVIRONMENT, which is process-global and set by
                // whichever factory happened to construct last.
                { "Swagger:Enabled", "true" },
                // Seeded the way a deployment with no database row yet seeds it, so the precedence
                // tests have a configured value for a stored one to beat, and a value to fall back
                // to when the stored one is cleared.
                { "Resend:ApiKey", ConfiguredResendKey },
                // Deliberately not the JWT key. ConnectorOptions.Validate refuses a Connectors:Key
                // that matches another control's, so the test host proves that rule holds every run.
                { "Connectors:Key", "test-connectors-key-that-is-its-own-and-long-enough" },
                { "Feeds:SiteUrl", "https://test.example.com" },
                { "Feeds:Paths:sitemap_paths", "/articles/{slug}" },
                // The collection sync schedule is off here, and the tests that cover it drive the
                // sweep themselves. Left on, every host in the run would sweep every tenant once a
                // minute for ninety minutes, running whichever sync a test had just configured at a
                // moment the test did not choose.
                { "CollectionSyncs:Enabled", "false" },
                // The job queue, tuned for a test run: a retry waits nothing, so a job that fails
                // five times dead-letters in seconds. The backoff arithmetic itself is
                // JobBackoffTests, a unit test.
                //
                // The storage probe keeps its default of a minute. Every host a test builds with
                // WithSettings stays alive for the rest of the run (see WithSetting), and each one
                // runs a worker per command type that opens an unpooled connection on every probe.
                // At one second that was two connections a second per host, times the ninety-odd
                // hosts a full run accumulates, against the hundred postgres:16 allows: a new
                // host's schema apply then failed to connect, Program.cs logged the failure and
                // returned, and WebApplicationFactory reported a server that had never started. A
                // test that waits on a retry wakes the worker itself instead; see
                // TransactionalEnqueueTests.WaitForDeadLetterAsync.
                //
                // The lease is short for the same test: a claim the worker drops (that method says
                // how) is picked up again once the lease has passed, and no handler here runs for
                // more than a few seconds.
                { "Jobs:BackoffBaseSeconds", "0" },
                { "Jobs:LeaseSeconds", "5" },
                // Pages runs over its own probe type, so no other test's "page" entries meet its rules.
                { "Modules:Pages:ContentType", "pagetreeprobe" },
                { "Modules:Pages:MaxDepth", "3" },
                { "Modules:Pages:ReservedSlugs:0", "api" },
                { "Modules:Pages:ReservedSlugs:1", "Blog" },
                { "Modules:Pages:ReservedSlugs:2", "admin" },
                // Qr lists one field of one probe type, so the allowlist and the empty-allowlist
                // cases each have a type to use.
                { "Modules:Qr:Fields:qrprobe:0", "Token" },
                // Booking runs over its own probe type, so no other test's entries meet its overlap rule.
                { "Modules:Booking:ContentType", "bookingprobe" },
            });
        });

        // The test project references the Files module, so FastEndpoints discovers its endpoints.
        // Register the module's services + schema so those endpoints can activate (Postgres storage;
        // S3 stays dormant with no Files:S3 config). This also gives us a host that can test the
        // Files endpoints end to end.
        //
        // By hand, and with module discovery off for the process (see DiscoveryDefault), so this
        // host registers no IBarakoModule singletons: ModulesEndpointTests uses it as a deployment
        // running no modules, and the fakes below stand in for the real clients.
        builder.ConfigureServices((ctx, services) =>
        {
            // Let a test choose its own client IP so the auth rate limiter (5 attempts /
            // 15 min per IP) partitions per test instead of sharing one loopback bucket.
            // Without this, xunit v3 ordering packs enough auth calls into one window that a
            // later login gets 429 and a test asserting success fails. See TestRemoteIpFilter.
            services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter, TestRemoteIpFilter>();

            // The background scheduler does not run in tests.
            //
            // It waits thirty seconds after startup and then sweeps every minute, publishing
            // whatever is due. Every scheduling test drives SweepTenantAsync directly, so it adds
            // nothing, and it is a third writer nobody asked for: it can publish a test's draft
            // between the arrange and the act, or collide with a sweep a test is running itself.
            //
            // That is why #424 failed only in CI. A class in isolation finishes inside the thirty
            // second delay, so the timer never fires and the test passes every time locally. A full
            // suite runs for six minutes and gets six sweeps, any of which can land on a test's
            // item. Both recorded failure modes are consistent with it: a title reverted to v1 when
            // the sweeper published before the editor committed, and a document saying Draft against
            // a stream replaying to Published when it published after.
            //
            // Removed by implementation type rather than by clearing IHostedService, which would
            // also take the projection daemon and anything a module registers. The assertion below
            // is the point: a rename or a re-registration must fail here rather than quietly restore
            // the timer and make the suite intermittent again.
            var sweeper = services.SingleOrDefault(d =>
                d.ImplementationType == typeof(barakoCMS.Infrastructure.Services.ScheduledContentService));
            if (sweeper is null)
            {
                throw new InvalidOperationException(
                    "ScheduledContentService is no longer registered the way this fixture expects, so "
                  + "the background sweeper may still be running in tests. See #424.");
            }

            services.Remove(sweeper);

            // The retention sweep, for the same reason and with the same assertion. It deletes
            // finished workflow runs, so leaving it running would let it delete a run a test had just
            // created and asserted on, and the failure would look like the runner losing work.
            var retention = services.SingleOrDefault(d =>
                d.ImplementationType == typeof(barakoCMS.Features.Workflows.WorkflowRunRetentionService));
            if (retention is null)
            {
                throw new InvalidOperationException(
                    "WorkflowRunRetentionService is no longer registered the way this fixture expects, "
                  + "so the retention sweep may still be running in tests and deleting their runs.");
            }

            services.Remove(retention);

            // The delivery log sweep, for the same reason: it would delete a row a test seeded as
            // old on purpose to prove the sweep removes it.
            var deliveryRetention = services.SingleOrDefault(d =>
                d.ImplementationType == typeof(barakoCMS.Features.WebhookDeliveries.WebhookDeliveryRetentionService));
            if (deliveryRetention is null)
            {
                throw new InvalidOperationException(
                    "WebhookDeliveryRetentionService is no longer registered the way this fixture expects, "
                  + "so the delivery log sweep may still be running in tests.");
            }

            services.Remove(deliveryRetention);

            // The job dead letter sweep, for the same reason: a test seeds old dead letters on
            // purpose to prove the sweep removes them, and drives the sweep itself.
            var jobRetention = services.SingleOrDefault(d =>
                d.ImplementationType == typeof(barakoCMS.Infrastructure.Jobs.JobDeadLetterRetentionService));
            if (jobRetention is null)
            {
                throw new InvalidOperationException(
                    "JobDeadLetterRetentionService is no longer registered the way this fixture expects, "
                  + "so the job dead letter sweep may still be running in tests.");
            }

            services.Remove(jobRetention);

            // The idempotency key sweep, for the same reason: a test seeds expired records on
            // purpose to prove the sweep removes them, and drives the sweep itself.
            var idempotencyRetention = services.SingleOrDefault(d =>
                d.ImplementationType == typeof(barakoCMS.Infrastructure.Services.IdempotencyRetentionService));
            if (idempotencyRetention is null)
            {
                throw new InvalidOperationException(
                    "IdempotencyRetentionService is no longer registered the way this fixture expects, "
                  + "so the idempotency key sweep may still be running in tests.");
            }

            services.Remove(idempotencyRetention);

            // The startup pass that redacts stored execution logs, for the same reason: it would
            // rewrite a log a test seeded unredacted on purpose, before the test gets to look at it.
            var logRedaction = services.SingleOrDefault(d =>
                d.ImplementationType == typeof(barakoCMS.Features.Workflows.WorkflowExecutionLogRedactionService));
            if (logRedaction is null)
            {
                throw new InvalidOperationException(
                    "WorkflowExecutionLogRedactionService is no longer registered the way this fixture expects, "
                  + "so it may still rewrite logs tests seed unredacted.");
            }

            services.Remove(logRedaction);

            // Only this fixture's own host runs the workflow runner. Every host a test derives with
            // WithWebHostBuilder shares this database and lives until another class derives a host, and each
            // one polled the queue with its own services: its own recording transport, or the mock
            // or failing one a test put there. A run a test queued could be claimed by any of them,
            // so its email landed in a recorder nobody reads, or failed on a provider the test never
            // chose. The fixture is always built first, in InitializeAsync, so any later build is a
            // derived host. A test that needs a particular host to run an attempt builds a
            // WorkflowRunner on that host's services and calls RunOnceAsync.
            if (Interlocked.Increment(ref _hostsBuilt) > 1)
            {
                var runner = services.SingleOrDefault(d =>
                    d.ImplementationType == typeof(barakoCMS.Features.Workflows.WorkflowRunner));
                if (runner is null)
                {
                    throw new InvalidOperationException(
                        "WorkflowRunner is no longer registered the way this fixture expects, so derived "
                      + "hosts may be claiming runs queued on the fixture.");
                }

                services.Remove(runner);
            }

            new BarakoCMS.Email.Resend.ResendEmailModule().ConfigureServices(services, ctx.Configuration);
            services.ConfigureMarten(opts => ConfigureVia(new BarakoCMS.Email.Resend.ResendEmailModule(), opts));

            new BarakoCMS.Files.FilesModule().ConfigureServices(services, ctx.Configuration);
            services.ConfigureMarten(opts => ConfigureVia(new BarakoCMS.Files.FilesModule(), opts));

            // Files.S3 after Files, as a host registers it. Dormant with no bucket configured, so
            // every other test still stores in Postgres; it is here for the S3 provider a tenant can
            // choose through /api/settings/storage, and for the keys a derived host sets.
            new BarakoCMS.Files.S3.S3FilesModule().ConfigureServices(
                services, ctx.Configuration.GetSection("Modules:Files.S3"));

            // AI module: register its schema/endpoints, but swap the Ollama client for a deterministic
            // fake so the search tests run without an embedding backend.
            new BarakoCMS.AI.AiModule().ConfigureServices(services, ctx.Configuration);
            services.ConfigureMarten(opts => ConfigureVia(new BarakoCMS.AI.AiModule(), opts));
            services.RemoveAll<BarakoCMS.AI.IEmbeddingClient>();
            services.AddSingleton<BarakoCMS.AI.IEmbeddingClient, FakeEmbeddingClient>();

            // Accounting: registers its content lifecycle hooks (the balance invariant and the
            // chart-of-accounts rules), so the generic /api/contents endpoint can be tested with
            // real domain rules attached — the whole point of modelling accounting as content.
            new BarakoCMS.Accounting.AccountingModule().ConfigureServices(services, ctx.Configuration);
            services.ConfigureMarten(opts => ConfigureVia(new BarakoCMS.Accounting.AccountingModule(), opts));

            // The fixture module: a capability, a content hook and a gated endpoint for the tests
            // of core mechanisms that need a module and not a domain.
            new BarakoCMS.Testing.Fixture.FixtureModule().ConfigureServices(services, ctx.Configuration);

            // Diagnostics: the test project references it, so FastEndpoints already discovers
            // /api/client-errors — register its schema too so those endpoints can actually run.
            services.ConfigureMarten(opts => ConfigureVia(new BarakoCMS.Diagnostics.DiagnosticsModule(), opts));

            // Pwa: same reason — the assembly is referenced, so /api/pwa/* is discovered; register the
            // schema so those endpoints can run.
            services.ConfigureMarten(opts => ConfigureVia(new BarakoCMS.Pwa.PwaModule(), opts));

            // Pages: its hook guards the probe type configured above, and its three endpoints read
            // the same options. Registered with its own section, as the host scopes it.
            new BarakoCMS.Pages.PagesModule().ConfigureServices(services, ctx.Configuration.GetSection("Modules:Pages"));

            // Qr: the route reads its allowlist from the options registered here.
            new BarakoCMS.Qr.QrModule().ConfigureServices(services, ctx.Configuration.GetSection("Modules:Qr"));
            // Booking: its hook guards the probe type configured above, and the availability route reads
            // the same options. Registered with its own section, as the host scopes it.
            new BarakoCMS.Booking.BookingModule().ConfigureServices(services, ctx.Configuration.GetSection("Modules:Booking"));
            services.ConfigureMarten(opts => ConfigureVia(new BarakoCMS.Booking.BookingModule(), opts));

            // Forms: the submit endpoint is anonymous and rate limited, so its policy, its options and
            // its schema all have to be registered for /api/public/forms/* to answer at all. Scoped to
            // its own section, as the host scopes it.
            new BarakoCMS.Forms.FormsModule().ConfigureServices(services, ctx.Configuration.GetSection("Modules:Forms"));
            services.ConfigureMarten(opts => ConfigureVia(new BarakoCMS.Forms.FormsModule(), opts));
            services.ConfigureMarten(opts => ConfigureVia(new BarakoCMS.Reports.ReportsModule(), opts));

            // Payments: the checkout and webhook endpoints need the module's policy and schema, and a
            // provider to talk to. The fake signs with a fixed secret so a test can sign or tamper.
            new BarakoCMS.Payments.PaymentsModule().ConfigureServices(services, ctx.Configuration.GetSection("Modules:Payments"));
            services.ConfigureMarten(opts => ConfigureVia(new BarakoCMS.Payments.PaymentsModule(), opts));
            services.AddSingleton<BarakoCMS.Payments.IPaymentProvider, BarakoCMS.Tests.Features.Payments.FakePaymentProvider>();

            // Portability: its contents section, so a bundle applies through the config engine. Called
            // through IBarakoModule, the way the host calls every module's ConfigureServices.
            ((barakoCMS.Modules.IBarakoModule)new BarakoCMS.Portability.PortabilityModule()).ConfigureServices(services, ctx.Configuration);

            // Diagnostics: the incidents view reads its sources from the container, and the report
            // endpoint hands reports to the forwarder, which stays off unless a test turns it on.
            // Scoped to its own section, as the host scopes it.
            new BarakoCMS.Diagnostics.DiagnosticsModule().ConfigureServices(services, ctx.Configuration.GetSection("Modules:Diagnostics"));

            // FeatureFlags: /api/feature-flags is anonymous, so which keys it hands out is a test
            // this project has to be able to run.
            new BarakoCMS.FeatureFlags.FeatureFlagsModule().ConfigureServices(services, ctx.Configuration);
            services.ConfigureMarten(opts => ConfigureVia(new BarakoCMS.FeatureFlags.FeatureFlagsModule(), opts));

            // Scheduling: the sweep tests drive ScheduleSweepService directly, so only the document is needed.
            services.ConfigureMarten(opts => ConfigureVia(new BarakoCMS.Scheduling.SchedulingModule(), opts));

            // ExternalAuth: the OAuth start/callback endpoints are anonymous, so what they refuse
            // is only checkable over real HTTP. ConfigureServices gives them IHttpClientFactory.
            new BarakoCMS.ExternalAuth.ExternalAuthModule().ConfigureServices(services, ctx.Configuration);
            services.ConfigureMarten(opts => ConfigureVia(new BarakoCMS.ExternalAuth.ExternalAuthModule(), opts));

            // DeviceTrust: replaces core's no-op gate and contributes a global pre-processor.
            // DeviceTrust:Enforce is unset here, so the processor returns before doing anything and
            // the gate behaves like the no-op it replaces. The enforcement tests turn it on with
            // WithSettings, which builds a separate host.
            new BarakoCMS.DeviceTrust.DeviceTrustModule().ConfigureServices(services, ctx.Configuration);
            services.ConfigureMarten(opts => ConfigureVia(new BarakoCMS.DeviceTrust.DeviceTrustModule(), opts));

            // Analytics.Umami: registered with its own section, exactly as the host scopes it, and
            // with the outbound handler stubbed. A test that let this reach the network would be
            // asserting about the internet.
            new BarakoCMS.Analytics.Umami.UmamiAnalyticsModule()
                .ConfigureServices(services, ctx.Configuration.GetSection(
                    BarakoCMS.Analytics.Umami.UmamiOptions.SectionName));
            services.AddHttpClient<BarakoCMS.Analytics.Umami.IUmamiClient, BarakoCMS.Analytics.Umami.UmamiClient>()
                .ConfigurePrimaryHttpMessageHandler(() => new UmamiStubHandler());

            // The throwing action WorkflowRunTests uses, registered on the shared fixture rather
            // than only on the host that test builds. The fixture's hosted runner polls the same
            // database and can claim the attempt first; without the action registered here it
            // records "No handler is registered for action type" instead of the exception the test
            // is about.
            services.AddScoped<barakoCMS.Features.Workflows.IWorkflowAction, BarakoCMS.Tests.Features.Workflows.ThrowingRunnerAction>();
            services.AddScoped<barakoCMS.Features.Workflows.IWorkflowAction, BarakoCMS.Tests.Features.Workflows.CredentialEchoAction>();
            services.AddScoped<barakoCMS.Features.Workflows.IWorkflowAction, BarakoCMS.Tests.Features.Workflows.DeletedEchoAction>();
            services.AddScoped<barakoCMS.Features.Workflows.IWorkflowAction, BarakoCMS.Tests.Features.Workflows.CountingRunnerAction>();
            services.AddScoped<barakoCMS.Features.Workflows.IWorkflowAction, BarakoCMS.Tests.Features.Workflows.HookedRunnerAction>();
            services.AddScoped<barakoCMS.Features.Workflows.IWorkflowAction, BarakoCMS.Tests.Features.Workflows.SlowRunnerAction>();
            services.AddScoped<barakoCMS.Features.Workflows.IWorkflowAction, BarakoCMS.Tests.Features.Workflows.MeteredRunnerAction>();
            services.AddScoped<barakoCMS.Features.Workflows.IWorkflowAction, BarakoCMS.Tests.Features.Workflows.OutputRunnerAction>();
            services.AddScoped<barakoCMS.Features.Workflows.IWorkflowAction, BarakoCMS.Tests.Features.Workflows.RecordingRunnerAction>();
            services.AddScoped<barakoCMS.Features.Workflows.IWorkflowAction, BarakoCMS.Tests.Features.Workflows.FileRunnerAction>();
            services.AddScoped<barakoCMS.Features.Workflows.IWorkflowAction, BarakoCMS.Tests.Features.Workflows.UndeclaredFileRunnerAction>();
            services.AddScoped<barakoCMS.Features.Workflows.IWorkflowAction, BarakoCMS.Tests.Features.Workflows.ManyFilesRunnerAction>();
            services.AddScoped<barakoCMS.Features.Workflows.IWorkflowAction, BarakoCMS.Tests.Features.Workflows.SavedFileRunnerAction>();
            services.AddScoped<barakoCMS.Features.Workflows.IWorkflowAction, BarakoCMS.Tests.Features.Workflows.AttemptHistoryRunnerAction>();
            services.ConfigureMarten(opts => opts.Listeners.Add(new BarakoCMS.Tests.Features.Workflows.RunnerSaveRaceListener()));
            // A second executor, so a test can route a workflow away from the built-in runner.
            services.AddSingleton<barakoCMS.Features.Workflows.IWorkflowExecutor, BarakoCMS.Tests.Features.Workflows.ElsewhereExecutor>();

            // Email transport, replacing the Resend provider the module above registered. Resend
            // throws on every call here because no API key is configured, so any flow that emails
            // something has been running against a transport that always fails. Registration needs
            // the opposite: a test has to be able to read a token that only exists in an email.
            services.RemoveAll<barakoCMS.Core.Interfaces.IEmailService>();
            services.AddSingleton<RecordingEmailService>();

            // Every host this fixture or a test derived from it starts, so teardown can stop each
            // one's projection coordinator before the host's own stop reaches it (#1063).
            services.AddHostedService(sp => new StartedHostTracker(_startedHosts, sp));
            services.AddSingleton<barakoCMS.Core.Interfaces.IEmailService>(
                sp => sp.GetRequiredService<RecordingEmailService>());

            // FastEndpoints 8 discovers endpoints eagerly inside AddFastEndpoints, which ran in
            // Program.cs before any module assembly above was loaded, so none of the module
            // endpoints exist in that scan. Re-register with the module assemblies explicit;
            // FE registers EndpointData with a plain AddSingleton, so this last one wins.
            //
            // It skips the assemblies core's scan skips. LegacyModuleHostTests loads one that
            // cannot load into this process, and without the filter every later host fails here.
            services.AddFastEndpoints(o =>
            {
                o.Assemblies = ModuleEndpointAssemblies;
                o.AssemblyFilter = barakoCMS.Extensions.ServiceCollectionExtensions.LoadsOrIsSkipped;
            });
        });
    }

    /// <summary>
    /// Every module assembly whose endpoints this host serves. Named rather than inlined so a test
    /// that has to rebuild the host cannot quietly serve a smaller API than the fixture does.
    /// </summary>
    public static readonly System.Reflection.Assembly[] ModuleEndpointAssemblies =
    [
        typeof(BarakoCMS.Email.Resend.ResendEmailModule).Assembly,
        typeof(BarakoCMS.Files.FilesModule).Assembly,
        typeof(BarakoCMS.AI.AiModule).Assembly,
        typeof(BarakoCMS.Accounting.AccountingModule).Assembly,
        typeof(BarakoCMS.Testing.Fixture.FixtureModule).Assembly,
        typeof(BarakoCMS.Diagnostics.DiagnosticsModule).Assembly,
        typeof(BarakoCMS.Pwa.PwaModule).Assembly,
        typeof(BarakoCMS.Pages.PagesModule).Assembly,
        typeof(BarakoCMS.Qr.QrModule).Assembly,
        typeof(BarakoCMS.Booking.BookingModule).Assembly,

        typeof(BarakoCMS.Forms.FormsModule).Assembly,
        typeof(BarakoCMS.Reports.ReportsModule).Assembly,
        typeof(BarakoCMS.Payments.PaymentsModule).Assembly,
        typeof(BarakoCMS.FeatureFlags.FeatureFlagsModule).Assembly,
        // Portability owns no documents of its own, so beside its endpoints it needs only the
        // config section it registers, which ConfigureWebHost adds.
        typeof(BarakoCMS.Portability.PortabilityModule).Assembly,
        typeof(BarakoCMS.ExternalAuth.ExternalAuthModule).Assembly,
        typeof(BarakoCMS.DeviceTrust.DeviceTrustModule).Assembly,
        typeof(BarakoCMS.Import.ImportModule).Assembly,
        typeof(BarakoCMS.Analytics.Umami.UmamiAnalyticsModule).Assembly,
    ];

    /// <summary>
    /// What the host tried to email, for the flows whose only output is a message. Valid for clients
    /// made with <c>CreateClient()</c>; a host built by <c>WithWebHostBuilder</c> has its own.
    /// </summary>
    public RecordingEmailService Email => Services.GetRequiredService<RecordingEmailService>();

    public async ValueTask InitializeAsync()
    {
        await _postgresContainer.StartAsync();
        // Explicitly set Env Vars to ensure they are available for Program.cs builder
        Environment.SetEnvironmentVariable("DATABASE_URL", ConnectionString);
        Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection", ConnectionString);
        Environment.SetEnvironmentVariable("SKIP_SEEDER", "true");
        // The Memory health check reads the private memory of the process, and a full run keeps
        // every host it built alive in this one process, which passes the default 4096 MB ceiling.
        // Liveness and readiness then answer 503 for reasons no test chose. An environment
        // variable, because AddBarakoCMS reads the ceiling while it registers the check, before
        // the in-memory settings below are applied. HealthProbeTests proves the check itself still
        // fails at a low ceiling. See #1081.
        Environment.SetEnvironmentVariable("HealthChecks__MaxPrivateMemoryMegabytes", "1048576");

        // Canonical system roles with their well-known ids, before any test runs.
        // xunit v3 changed execution order inside the Sequential collection, and
        // several tests create a role named SuperAdmin with a random id when none
        // exists by name; the fixed-id insert that used to run first then dies on
        // the unique name index. Seeding here makes role state order-proof.
        using var scope = Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession();
        foreach (var role in new[]
                 {
                     new barakoCMS.Models.Role { Id = barakoCMS.Data.DataSeeder.SuperAdminRoleId, Name = "SuperAdmin", Description = "Full system access" },
                     new barakoCMS.Models.Role { Id = barakoCMS.Data.DataSeeder.AdminRoleId, Name = "Admin", Description = "Administrator with full access" },
                     // What a database seeded before HR moved behind the demo content still holds.
                     new barakoCMS.Models.Role { Id = barakoCMS.Data.DataSeeder.DemoHrRoleId, Name = "HR", Description = "Human Resources - manage attendance" },
                     new barakoCMS.Models.Role { Id = barakoCMS.Data.DataSeeder.UserRoleId, Name = "User", Description = "Standard user" },
                 })
        {
            var existing = await session.Query<barakoCMS.Models.Role>()
                .FirstOrDefaultAsync(r => r.Name == role.Name);

            // Through the same call the real seeder makes, so a system role here holds what a system
            // role holds in production. Building them by hand without it gave the fixture an "Admin"
            // that carried no capabilities at all, which was invisible while
            // Auth:LegacyRoleFallback let the role name open the gate, and which turned into
            // forty-odd failures the moment that default went off. A test double that does not match
            // the thing it doubles hides exactly the behaviour you are trying to test.
            if (existing is null)
            {
                barakoCMS.Data.DataSeeder.ApplyCapabilityDefaults(role);
                barakoCMS.Data.DataSeeder.GrantSensitiveToSeededHr(role);
                barakoCMS.Data.DataSeeder.MarkPlatform(role);
                session.Store(role);
            }
            else if (barakoCMS.Data.DataSeeder.ApplyCapabilityDefaults(existing)
                     | barakoCMS.Data.DataSeeder.GrantSensitiveToSeededHr(existing)
                     | barakoCMS.Data.DataSeeder.MarkPlatform(existing))
            {
                session.Store(existing);
            }
        }
        await session.SaveChangesAsync();

        // The modules registered above seed too, and a module's capabilities reach the Admin role
        // only from its own seeder, because core cannot name them. Skipping this gave the fixture a
        // host that had every module's endpoints and none of the capabilities those endpoints ask
        // for, which was invisible while the role-name fallback answered instead. BarakoCMS.Suite
        // makes the same call for the same reason, right after the core seeder.
        var modules = DiscoverModules();
        if (modules.Count < 8)
        {
            // Finding almost none means the discovery stopped working rather than that the modules
            // stopped existing, and a fixture that silently seeds nothing is what this replaced.
            throw new InvalidOperationException(
                $"Only {modules.Count} module(s) were discovered to seed. The suite references every "
              + "first-party module, so this is a discovery failure rather than a real answer.");
        }

        foreach (var module in modules)
        {
            using var moduleScope = Services.CreateScope();
            var moduleSession = moduleScope.ServiceProvider.GetRequiredService<IDocumentSession>();
            await module.SeedAsync(moduleSession, moduleScope.ServiceProvider, CancellationToken.None);
            await moduleSession.SaveChangesAsync();
        }
    }

    /// <summary>
    /// The modules this fixture configures, so it can seed the same ones it wires up.
    /// </summary>
    /// <remarks>
    /// A module's capabilities reach the Admin role only from its own seeder, because core cannot
    /// name them. The fixture configured seven modules' services and seeded none of them, so their
    /// endpoints were present and the capabilities those endpoints ask for were not, which was
    /// invisible while the role-name fallback answered instead.
    ///
    /// Seeded directly rather than through <c>RunBarakoModuleSeedersAsync</c>, which reads
    /// <c>IBarakoModule</c> singletons out of the container. This fixture deliberately registers
    /// none: <see cref="ModulesEndpointTests"/> uses the shared host as a deployment running no
    /// modules, and registering them here would take that case away. Same instances, same seeds,
    /// one list.
    /// </remarks>
    /// <summary>
    /// Every module type the loaded assemblies define, constructed and seeded.
    /// </summary>
    /// <remarks>
    /// A module's capabilities reach the Admin role only from its own seeder, because core cannot
    /// name them. The fixture configured module services and seeded none of them, so their endpoints
    /// were present and the capabilities those endpoints ask for were not, which was invisible while
    /// the role-name fallback answered instead.
    ///
    /// Discovered rather than listed. The first version of this was a hand-written array of eleven,
    /// and it was stale within the day: BarakoCMS.Import gained a capability and a seeder in #494 and
    /// was not in it, so three tests failed on a capability nothing had granted. A list of modules
    /// maintained beside the modules is the same defect the capability gates were changed to stop
    /// relying on.
    /// </remarks>
    private static IReadOnlyList<barakoCMS.Modules.IBarakoModule> DiscoverModules() =>
        AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.GetName().Name?.StartsWith("BarakoCMS.", StringComparison.Ordinal) == true)
            .SelectMany(LoadableTypes.In)
            .Where(t => t is { IsAbstract: false, IsInterface: false }
                     && typeof(barakoCMS.Modules.IBarakoModule).IsAssignableFrom(t)
                     && t.GetConstructor(Type.EmptyTypes) is not null)
            .Select(t => (barakoCMS.Modules.IBarakoModule)Activator.CreateInstance(t)!)
            .ToList();


    public new async ValueTask DisposeAsync()
    {
        await StopProjectionCoordinatorsAsync(_startedHosts.Reverse());

        // The hosts first. Stopping one stops the projection daemon and the workflow runner, and with
        // the database already gone those fail their passes and Marten's coordinator threw
        // ObjectDisposedException out of StopAsync, failing every test in the collection.
        // A derived host is not one the root made, so the root's disposal does not reach it.
        List<DerivedHost> derived;
        lock (_derivedGate)
        {
            derived = [.. _derived];
            _derived.Clear();
        }

        var failures = new List<Exception>();
        FastEndpointsResolver.Exclusively(() =>
            failures.AddRange(DisposeEach(derived, "when the fixture was disposed")));

        try
        {
            await base.DisposeAsync();
        }
        catch (Exception e)
        {
            failures.Add(e);
        }

        _fallbacks.Dispose();
        try
        {
            await _postgresContainer.DisposeAsync();
        }
        catch (Exception e)
        {
            failures.Add(e);
        }

        if (failures.Count > 0)
        {
            throw new AggregateException(
                $"IntegrationTestFixture: {failures.Count} failure(s) while disposing the fixture (#1171).", failures);
        }

        // Anything still up now is a host the fixture lost track of, and the run would have kept
        // it polling.
        var leftover = _startedHosts.Count(IsUp);
        if (leftover > 0)
        {
            throw new InvalidOperationException(
                $"IntegrationTestFixture: {leftover} host(s) were still running after the fixture was disposed (#1171).");
        }
    }

    private static bool IsUp(IServiceProvider services)
    {
        try
        {
            services.GetService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Stops the projection coordinator of every host that started, before any host stops (#1063).
    /// </summary>
    /// <remarks>
    /// <para>
    /// JasperFx.Events 2.75.2, <c>ProjectionCoordinatorBase.stopRunnerAsync</c>: line 151 returns when
    /// there is no loop, line 155 cancels the loop's token source, and lines 160 and 161 dispose it and
    /// clear the field, with no lock around any of it. A disposed source at line 155 can only be one
    /// another stop of the same coordinator disposed while this one was already past line 151, so the
    /// throw on 2 October, out of the host's own stop inside <c>WebApplicationFactory.DisposeAsync</c>,
    /// was two stops of one coordinator overlapping.
    /// </para>
    /// <para>
    /// Stopping each coordinator here, once and awaited, while every host and the database are still
    /// up, leaves the field cleared before the host's stop, anything that stop sets off, or the
    /// container's disposal of the coordinator calls it again. Each of those then returns at line 151
    /// and touches no token source. Nothing in barakoCMS starts a coordinator again after that: nothing
    /// calls <c>ResumeAsync</c>.
    /// </para>
    /// <para>
    /// Only one throw is caught: an <see cref="ObjectDisposedException"/> from a coordinator's stop is
    /// written to stderr and teardown carries on. Anything else, a timeout included, still fails the
    /// collection.
    /// </para>
    /// </remarks>
    private static async Task StopProjectionCoordinatorsAsync(IEnumerable<IServiceProvider> hosts)
    {
        foreach (var services in hosts)
        {
            Marten.Events.Daemon.Coordination.IProjectionCoordinator? coordinator;
            try
            {
                coordinator = services.GetService<Marten.Events.Daemon.Coordination.IProjectionCoordinator>();
            }
            catch (ObjectDisposedException)
            {
                // A host a test disposed itself, which stopped its coordinator on the way.
                continue;
            }

            if (coordinator is null)
            {
                continue;
            }

            try
            {
                await coordinator.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
            }
            catch (ObjectDisposedException e)
            {
                await Console.Error.WriteLineAsync(
                    "IntegrationTestFixture: a projection coordinator threw ObjectDisposedException while stopping "
                    + $"at teardown (#1063). Teardown carries on; no test failed because of it.{Environment.NewLine}{e}");
            }
        }
    }

    /// <summary>Records the provider of a host once it has started, for teardown.</summary>
    private sealed class StartedHostTracker(
        System.Collections.Concurrent.ConcurrentQueue<IServiceProvider> started,
        IServiceProvider services) : Microsoft.Extensions.Hosting.IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            started.Enqueue(services);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>
    /// Creates a JWT token using standard libraries, avoiding FastEndpoints' static ServiceResolver issue.
    /// </summary>
    /// <summary>
    /// A host with one configuration value overridden, for tests about configuration-dependent
    /// behaviour. Null removes the key, which is the case worth testing: a setting nobody set.
    ///
    /// Do not dispose the result. The fixture disposes it once the class that asked for it is done
    /// (see <see cref="WithWebHostBuilder"/>).
    /// </summary>
    public WebApplicationFactory<Program> WithSetting(string key, string? value) =>
        WithSettings(new Dictionary<string, string?> { { key, value } });

    /// <summary>
    /// The same, for behaviour that only appears once several settings agree. Turning a module on
    /// usually takes more than one key, and setting them one host at a time gets you a host that
    /// has the flag but not the URL.
    ///
    /// Do not dispose the result, for the reason given above.
    /// </summary>
    public WebApplicationFactory<Program> WithSettings(IDictionary<string, string?> settings) =>
        WithWebHostBuilder(Settings(settings));

    /// <summary>
    /// <see cref="WithSettings"/> for a host cached where more than one test class reaches it.
    /// Disposed with the fixture rather than with the class that first asked.
    /// </summary>
    public WebApplicationFactory<Program> WithRunScopedSettings(IDictionary<string, string?> settings) =>
        WithRunScopedHost(Settings(settings));

    private static Action<IWebHostBuilder> Settings(IDictionary<string, string?> settings) =>
        b => b.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings));

    private static readonly Lock StoredCallers = new();
    private static readonly Dictionary<string, Task<string>> StoredTokens = new(StringComparer.Ordinal);

    /// <summary>
    /// A token for a user that exists, holding the seeded roles named.
    /// </summary>
    /// <remarks>
    /// <see cref="CreateToken"/> mints role claims and a random <c>UserId</c> with no stored user
    /// behind it. That is not a token this system issues: <c>TokenIssuer</c> mints claims for a user
    /// that exists, and a capability gate answers from the stored user's roles rather than from the
    /// claim. The difference was invisible while <c>Auth:LegacyRoleFallback</c> honoured the role
    /// name, which is exactly why forty-five tests broke the moment it was turned off.
    ///
    /// The role is the seeded one where the seeder made it, so it carries the capabilities the
    /// seeder and the backfill gave it. A name the seeder does not create gets a role with no
    /// capabilities, which is the correct answer and what makes a "wrong role" caller meaningful.
    ///
    /// Cached per role set. <see cref="RoleGateTests"/> records why: a hundred throwaway users sit
    /// at the top of <c>GET /api/users</c>, where other tests read the first row.
    /// </remarks>
    public Task<string> StoredUserTokenAsync(params string[] roleNames)
    {
        var key = string.Join('\u001f', roleNames);

        lock (StoredCallers)
        {
            if (StoredTokens.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var minted = MintForStoredUserAsync(roleNames);
            StoredTokens[key] = minted;
            return minted;
        }
    }

    private async Task<string> MintForStoredUserAsync(string[] roleNames)
    {
        using var scope = Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<Marten.IDocumentSession>();

        var roleIds = new List<Guid>();
        foreach (var roleName in roleNames)
        {
            // A tenant may own a role of the same name, and a test that stores one (an empty
            // "SuperAdmin" for the legacy-name fallback) must not hand every later caller its
            // empty capabilities. The role without a tenant is the seeded one.
            var named = await session.Query<barakoCMS.Models.Role>()
                .Where(r => r.Name == roleName)
                .ToListAsync();
            var role = named.OrderBy(r => r.TenantSlug is not null).FirstOrDefault();

            // A name the seeder does not create is left alone rather than invented. Such a role
            // holds nothing, so the user reaches nothing through it, which is the answer these
            // callers are for. Creating one would also take the name: role names are uniquely
            // indexed, and tests that create "Editor" through POST /api/roles then fail on a
            // document that this helper quietly made first.
            if (role is not null)
            {
                roleIds.Add(role.Id);
            }
        }

        var userId = Guid.NewGuid();
        session.Store(new barakoCMS.Models.User
        {
            Id = userId,
            Username = $"stored-{userId:n}",
            Email = $"stored-{userId:n}@example.com",
            RoleIds = roleIds,
        });
        await session.SaveChangesAsync();

        // Username as well as UserId, because TokenIssuer mints both and endpoints record the name
        // as the actor. Without it an endpoint that stores who did something reads null here and a
        // real sign-in never would.
        return CreateToken(roleNames, userId.ToString(),
            new Dictionary<string, string> { ["Username"] = $"stored-{userId:n}" });
    }

    public string CreateToken(string[] roles, string? userId = null, Dictionary<string, string>? additionalClaims = null)
    {
        var signingKey = "test-super-secret-key-that-is-at-least-32-chars-long";
        var issuer = "BarakoTest";
        var audience = "BarakoClient";

        var securityKey = new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(
            System.Text.Encoding.UTF8.GetBytes(signingKey));
        var credentials = new Microsoft.IdentityModel.Tokens.SigningCredentials(
            securityKey, Microsoft.IdentityModel.Tokens.SecurityAlgorithms.HmacSha256);

        var claims = new List<System.Security.Claims.Claim>();

        foreach (var role in roles)
        {
            claims.Add(new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, role));
        }

        claims.Add(new System.Security.Claims.Claim("UserId", userId ?? Guid.NewGuid().ToString()));

        // Matches what TokenIssuer mints. Without it the session epoch check has nothing to compare
        // against and serves every request, so a test suite using this helper would report the
        // control as working while it did nothing.
        claims.Add(new System.Security.Claims.Claim(
            System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Iat,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)));

        if (additionalClaims != null)
        {
            foreach (var kvp in additionalClaims)
            {
                claims.Add(new System.Security.Claims.Claim(kvp.Key, kvp.Value));
            }
        }

        var token = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            expires: DateTime.UtcNow.AddDays(1),
            signingCredentials: credentials);

        return new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().WriteToken(token);
    }
}
