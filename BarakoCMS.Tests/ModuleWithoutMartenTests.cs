using barakoCMS.Core.Interfaces;
using barakoCMS.Extensions;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using barakoCMS.Modules;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// #948: a module can seed, read content and run a lifecycle hook through barakoCMS types alone.
/// The probe types are in their own file, which has no Marten reference; the plumbing that stands
/// in for the host is here.
/// </summary>
[Collection("Integration1")]
public class ModuleWithoutMartenTests
{
    private readonly IntegrationTestFixture _factory;

    public ModuleWithoutMartenTests(IntegrationTestFixture factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string NewName(string stem) => $"{stem}-{Guid.NewGuid():N}";

    private static async Task<Content> StoreEntryAsync(IDocumentSession session, string type, string code)
    {
        var entry = new Content
        {
            Id = Guid.NewGuid(),
            ContentType = type,
            Data = new Dictionary<string, object> { ["Code"] = code },
            Status = ContentStatus.Published,
        };
        session.Store(entry);
        await session.SaveChangesAsync(Ct);
        return entry;
    }

    [Fact]
    public async Task The_probe_names_no_marten_type()
    {
        var source = await File.ReadAllTextAsync(ProbePath(), Ct);

        source.Should().NotMatchRegex(@"\bMarten\b");

        foreach (var type in new[]
                 {
                     typeof(NoMartenProbeModule), typeof(NoMartenProbeHook),
                     typeof(NoMartenLockHook), typeof(NoMartenDocumentHook),
                 })
        {
            var signatures = type.GetMembers(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.DeclaredOnly)
                .OfType<System.Reflection.MethodInfo>()
                .SelectMany(m => m.GetParameters().Select(p => p.ParameterType).Append(m.ReturnType));
            signatures.Should().NotBeEmpty();
            signatures.Should().NotContain(t => (t.Namespace ?? "").StartsWith("Marten", StringComparison.Ordinal));
        }
    }

    private static string ProbePath() =>
        Path.Combine(RepoPaths.Root, "BarakoCMS.Tests", "NoMartenProbe.cs");

    [Fact]
    public async Task A_module_seeds_a_content_type_and_a_role_without_naming_marten()
    {
        var type = NewName("probe-type");
        var roleName = NewName("probe-role");
        var roleId = Guid.NewGuid();
        var module = new NoMartenProbeModule(type, roleName, roleId);

        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            await ((IBarakoModule)module).SeedAsync(new ModuleSeedContext(session, scope.ServiceProvider), Ct);
            await session.SaveChangesAsync(Ct);
        }

        module.ContentTypeAdded.Should().BeTrue();
        module.RoleAdded.Should().BeTrue();

        using var check = _factory.Services.CreateScope();
        var query = check.ServiceProvider.GetRequiredService<IQuerySession>();
        (await query.Query<ContentTypeDefinition>().Where(d => d.Name == type).ToListAsync(Ct)).Should().HaveCount(1);
        (await query.LoadAsync<Role>(roleId, Ct))!.Name.Should().Be(roleName);
    }

    [Fact]
    public async Task Seeding_twice_adds_nothing_the_second_time()
    {
        var type = NewName("probe-type");
        var roleId = Guid.NewGuid();
        var module = new NoMartenProbeModule(type, NewName("probe-role"), roleId);

        for (var run = 0; run < 2; run++)
        {
            using var scope = _factory.Services.CreateScope();
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            await ((IBarakoModule)module).SeedAsync(new ModuleSeedContext(session, scope.ServiceProvider), Ct);
            await session.SaveChangesAsync(Ct);
        }

        module.ContentTypeAdded.Should().BeFalse("the second run finds the type the first one stored");
        module.RoleAdded.Should().BeFalse("the second run finds the role by its id");

        using var check = _factory.Services.CreateScope();
        var query = check.ServiceProvider.GetRequiredService<IQuerySession>();
        (await query.Query<ContentTypeDefinition>().Where(d => d.Name == type).ToListAsync(Ct)).Should().HaveCount(1);
    }

    [Fact]
    public async Task A_seed_reads_the_content_that_is_already_committed()
    {
        var type = NewName("probe-type");
        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            await StoreEntryAsync(session, type, "A");
            await StoreEntryAsync(session, type, "B");
        }

        var module = new NoMartenProbeModule(type, NewName("probe-role"), Guid.NewGuid());
        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            await ((IBarakoModule)module).SeedAsync(new ModuleSeedContext(session, scope.ServiceProvider), Ct);
        }

        module.SeenWhileSeeding.Should().Be(2);
    }

    [Fact]
    public async Task The_registered_reader_pages_counts_and_reads_every_entry_of_a_type()
    {
        var type = NewName("probe-type");
        var other = NewName("probe-other");
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var first = await StoreEntryAsync(session, type, "1");
        await StoreEntryAsync(session, type, "2");
        await StoreEntryAsync(session, type, "3");
        await StoreEntryAsync(session, other, "elsewhere");

        var reader = scope.ServiceProvider.GetRequiredService<IContentReader>();

        (await reader.CountAsync(type, Ct)).Should().Be(3);
        (await reader.GetAsync(first.Id, Ct))!.Data["Code"].ToString().Should().Be("1");
        (await reader.GetAsync(Guid.NewGuid(), Ct)).Should().BeNull();

        var pageOne = await reader.ListAsync(type, 0, 2, Ct);
        var pageTwo = await reader.ListAsync(type, 2, 2, Ct);
        pageOne.Should().HaveCount(2);
        pageTwo.Should().HaveCount(1);
        pageOne.Concat(pageTwo).Select(c => c.Id).Should().OnlyHaveUniqueItems();

        var all = new List<Content>();
        await foreach (var entry in reader.ReadAllAsync(type, Ct))
        {
            all.Add(entry);
        }

        all.Should().HaveCount(3);
        all.Should().OnlyContain(c => c.ContentType == type);
    }

    [Fact]
    public async Task A_hook_rejects_a_duplicate_and_enriches_the_data_through_the_reader()
    {
        var type = NewName("probe-type");
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        await StoreEntryAsync(session, type, "TAKEN");

        var runner = new ContentLifecycleRunner([new NoMartenProbeHook(type)], session);

        var duplicate = new Dictionary<string, object> { ["Code"] = "TAKEN" };
        var rejected = await runner.RunBeforeSaveAsync(type, null, duplicate, null, Guid.NewGuid(), Ct);

        rejected.Should().ContainSingle().Which.Should().Contain("TAKEN");
        duplicate.Should().ContainKey("Checked", "a hook enriches the live dictionary");

        var fresh = new Dictionary<string, object> { ["Code"] = "FREE" };
        (await runner.RunBeforeSaveAsync(type, null, fresh, null, Guid.NewGuid(), Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_hook_written_against_the_marten_context_still_runs()
    {
        var type = NewName("probe-type");
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var runner = new ContentLifecycleRunner([new LegacyHook(type)], session);

        var errors = await runner.RunBeforeSaveAsync(
            type, null, new Dictionary<string, object>(), null, Guid.NewGuid(), Ct);

        errors.Should().ContainSingle().Which.Should().Be("legacy hook ran with a session");
    }

    [Fact]
    public async Task A_module_written_against_the_marten_seed_is_still_seeded_by_the_host()
    {
        var legacy = new LegacyModule();
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        await ((IBarakoModule)legacy).SeedAsync(new ModuleSeedContext(session, scope.ServiceProvider), Ct);

        legacy.SeededWith.Should().BeSameAs(session);
    }

    [Fact]
    public async Task Reading_all_entries_with_a_small_page_returns_each_one_once()
    {
        var type = NewName("probe-type");
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var stored = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            stored.Add((await StoreEntryAsync(session, type, i.ToString())).Id);
        }

        var reader = new SessionContentReader(session, pageSize: 2);
        var all = new List<Content>();
        await foreach (var entry in reader.ReadAllAsync(type, Ct))
        {
            all.Add(entry);
        }

        all.Should().HaveCount(5, "three pages of 2, 2 and 1 are needed to reach every entry");
        all.Select(c => c.Id).Should().OnlyHaveUniqueItems();
        all.Select(c => c.Id).Should().BeEquivalentTo(stored);
    }

    [Fact]
    public async Task The_host_runner_seeds_a_module_that_only_overrides_the_marten_seed()
    {
        var store = _factory.Services.GetRequiredService<IDocumentStore>();
        var legacy = new LegacyModule();
        var services = new ServiceCollection()
            .AddSingleton<IBarakoModule>(legacy)
            .AddScoped<IDocumentSession>(_ => store.LightweightSession())
            .BuildServiceProvider();

        await services.RunBarakoModuleSeedersAsync(Ct);

        legacy.SeededWith.Should().NotBeNull("the runner calls the new member and the default forwards to the old one");
    }

    [Fact]
    public async Task The_default_seed_refuses_a_context_the_host_did_not_make()
    {
        IBarakoModule legacy = new LegacyModule();

        var act = () => legacy.SeedAsync(new ForeignSeedContext(), Ct);

        await act.Should().ThrowAsync<NotSupportedException>();
    }

    [Fact]
    public async Task The_default_hook_refuses_a_context_the_host_did_not_make()
    {
        IContentLifecycleHook legacy = new LegacyHook(NewName("probe-type"));

        var act = () => legacy.BeforeSaveAsync(new ForeignHookContext(), Ct);

        await act.Should().ThrowAsync<NotSupportedException>();
    }

    private static Task<IReadOnlyList<string>> RunHookAsync(IContentLifecycleHook hook, string type, IDocumentSession session) =>
        new ContentLifecycleRunner([hook], session)
            .RunBeforeSaveAsync(type, null, new Dictionary<string, object>(), null, Guid.NewGuid(), Ct);

    private static async Task WaitForAdvisoryWaiterAsync(IDocumentStore store, string tenant, string key)
    {
        // Matched on this test's own lock key, so another class waiting on some other advisory lock
        // in the same database does not end the wait. An advisory bigint key is split over classid
        // (high half) and objid (low half) in pg_locks.
        var lockKey = $"barakocms:hook-lock:{tenant.Length}:{tenant}:{key}";
        await using var watcher = store.LightweightSession();
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var waiting = await watcher.QueryAsync<int>(
                "select count(*)::int from pg_locks where locktype = 'advisory' and not granted "
                + "and ((classid::bigint << 32) | objid::bigint) = hashtextextended(?, 0)",
                Ct, lockKey);
            if (waiting.Single() > 0)
            {
                return;
            }

            await Task.Delay(50, Ct);
        }

        throw new TimeoutException("No session ever waited on this test's advisory lock.");
    }

    [Fact]
    public async Task A_hook_lock_is_held_until_the_write_commits_and_a_second_write_waits_for_it()
    {
        var type = NewName("probe-lock");
        var key = NewName("probe-key");
        var store = _factory.Services.GetRequiredService<IDocumentStore>();
        await using var first = store.LightweightSession();
        await using var second = store.LightweightSession();
        var hook = new NoMartenLockHook(type, key);

        (await RunHookAsync(hook, type, first)).Should().BeEmpty();
        var waiting = RunHookAsync(hook, type, second);
        await WaitForAdvisoryWaiterAsync(store, second.TenantId, key);

        waiting.IsCompleted.Should().BeFalse("the first hook has returned but its write has not committed");

        // The write commits something, as a real content save does, and the commit releases the lock.
        first.Store(new Role { Id = Guid.NewGuid(), Name = NewName("probe-role") });
        await first.SaveChangesAsync(Ct);

        (await waiting.WaitAsync(TimeSpan.FromSeconds(15), Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task The_same_hook_lock_key_in_two_tenants_does_not_block()
    {
        var type = NewName("probe-lock");
        var key = NewName("probe-key");
        var store = _factory.Services.GetRequiredService<IDocumentStore>();
        await using var inOne = store.LightweightSession(NewName("tenant-a"));
        await using var inTwo = store.LightweightSession(NewName("tenant-b"));
        var hook = new NoMartenLockHook(type, key);

        (await RunHookAsync(hook, type, inOne)).Should().BeEmpty();

        var other = RunHookAsync(hook, type, inTwo);
        (await other.WaitAsync(TimeSpan.FromSeconds(15), Ct)).Should().BeEmpty(
            "the lock key includes the tenant, so the other tenant's write does not wait");
    }

    [Fact]
    public async Task A_document_a_hook_stores_commits_with_the_content_save()
    {
        var type = NewName("probe-store");
        var roleId = Guid.NewGuid();
        var store = _factory.Services.GetRequiredService<IDocumentStore>();
        await using (var session = store.LightweightSession())
        {
            await RunHookAsync(new NoMartenDocumentHook(type, roleId, store: true), type, session);
            session.Store(new Role { Id = Guid.NewGuid(), Name = NewName("probe-role") });
            await session.SaveChangesAsync(Ct);
        }

        using var check = _factory.Services.CreateScope();
        var query = check.ServiceProvider.GetRequiredService<IQuerySession>();
        (await query.LoadAsync<ProbeDocument>(roleId, Ct))!.Name.Should().Be("stored-by-hook");
    }

    [Fact]
    public async Task A_document_a_hook_stores_is_gone_when_the_content_save_fails()
    {
        var type = NewName("probe-store");
        var roleId = Guid.NewGuid();
        var taken = await StoreTakenEntryAsync(type);
        var store = _factory.Services.GetRequiredService<IDocumentStore>();

        await using (var session = store.LightweightSession())
        {
            await RunHookAsync(new NoMartenDocumentHook(type, roleId, store: true), type, session);

            // The content write fails after the hook ran: an insert under an id that exists.
            session.Insert(new Content { Id = taken.Id, ContentType = type, Data = new Dictionary<string, object>() });
            var save = () => session.SaveChangesAsync(Ct);
            await save.Should().ThrowAsync<Exception>();
        }

        using var check = _factory.Services.CreateScope();
        var query = check.ServiceProvider.GetRequiredService<IQuerySession>();
        (await query.LoadAsync<ProbeDocument>(roleId, Ct)).Should().BeNull("the hook's store shares the failed save's transaction");
        (await query.LoadAsync<Content>(taken.Id, Ct)).Should().NotBeNull("the control: the entry that was already there");
    }

    private async Task<Content> StoreTakenEntryAsync(string type)
    {
        using var scope = _factory.Services.CreateScope();
        return await StoreEntryAsync(scope.ServiceProvider.GetRequiredService<IDocumentSession>(), type, "TAKEN");
    }

    [Fact]
    public async Task Load_in_the_same_write_returns_what_store_wrote_and_otherwise_reads_the_committed_row()
    {
        var type = NewName("probe-store");
        var storedId = Guid.NewGuid();
        var committedId = Guid.NewGuid();
        var store = _factory.Services.GetRequiredService<IDocumentStore>();
        await using (var arrange = store.LightweightSession())
        {
            arrange.Store(new ProbeDocument { Id = committedId, Name = "committed" });
            await arrange.SaveChangesAsync(Ct);
        }

        await using var session = store.LightweightSession();

        var writes = new NoMartenDocumentHook(type, storedId, store: true);
        await RunHookAsync(writes, type, session);
        writes.Loaded.Should().NotBeNull("the database has not seen the document yet, so the context answers");
        writes.Loaded!.Name.Should().Be("stored-by-hook");

        var reads = new NoMartenDocumentHook(type, committedId, store: false);
        await RunHookAsync(reads, type, session);
        reads.Loaded!.Name.Should().Be("committed");

        var missing = new NoMartenDocumentHook(type, Guid.NewGuid(), store: false);
        await RunHookAsync(missing, type, session);
        missing.Loaded.Should().BeNull();
    }

    [Fact]
    public async Task Two_contexts_on_one_session_share_what_the_first_stored()
    {
        var type = NewName("probe-counter");
        var counterId = Guid.NewGuid();
        var store = _factory.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession();
        var hook = new NoMartenCounterHook(type, counterId);

        // Two entries of one push: each runs its hooks in its own context before anything is saved.
        await RunHookAsync(hook, type, session);
        await RunHookAsync(hook, type, session);
        await session.SaveChangesAsync(Ct);

        using var check = _factory.Services.CreateScope();
        var query = check.ServiceProvider.GetRequiredService<IQuerySession>();
        (await query.LoadAsync<ProbeDocument>(counterId, Ct))!.Count.Should().Be(2);
    }

    [Fact]
    public async Task A_load_after_a_save_reads_the_committed_row_not_the_old_staged_one()
    {
        var type = NewName("probe-counter");
        var counterId = Guid.NewGuid();
        var store = _factory.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession();
        var hook = new NoMartenCounterHook(type, counterId);

        await RunHookAsync(hook, type, session);
        await session.SaveChangesAsync(Ct);

        await using (var other = store.LightweightSession())
        {
            other.Store(new ProbeDocument { Id = counterId, Name = "counter", Count = 10 });
            await other.SaveChangesAsync(Ct);
        }

        await RunHookAsync(hook, type, session);
        await session.SaveChangesAsync(Ct);

        using var check = _factory.Services.CreateScope();
        var query = check.ServiceProvider.GetRequiredService<IQuerySession>();
        (await query.LoadAsync<ProbeDocument>(counterId, Ct))!.Count.Should().Be(11);
    }

    [Fact]
    public async Task A_core_document_cannot_be_stored_or_loaded_through_the_context()
    {
        var type = NewName("probe-core");
        var store = _factory.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession();

        var load = () => RunHookAsync(new NoMartenCoreDocumentHook(type), type, session);
        (await load.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("Role");

        var context = new ContentLifecycleContext
        {
            ContentType = type,
            Data = new Dictionary<string, object>(),
#pragma warning disable CS0618
            Session = session,
#pragma warning restore CS0618
            UserId = Guid.NewGuid(),
        };
        var roleId = Guid.NewGuid();
        var storeRole = () => ((IContentHookContext)context).Store(new Role { Id = roleId, Name = "core" });
        storeRole.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("Role");
        session.PendingChanges.AllChangedFor<Role>().Where(r => r.Id == roleId).Should().BeEmpty();
    }

    private sealed class ForeignSeedContext : IModuleSeedContext
    {
        public IServiceProvider Services => throw new NotImplementedException();

        public IContentReader Reader => throw new NotImplementedException();

        public IContentWriter Writer => throw new NotImplementedException();

        public Task<bool> EnsureContentTypeAsync(ContentTypeDefinition definition, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<bool> EnsureRoleAsync(Guid id, string name, string description, CancellationToken cancellationToken) =>
            throw new NotImplementedException();
    }

    private sealed class ForeignHookContext : IContentHookContext
    {
        public string ContentType => "foreign";

        public Dictionary<string, object> Data { get; } = new();

        public IReadOnlyDictionary<string, object>? Existing => null;

        public Guid? EntryId => null;

        public Guid UserId => Guid.Empty;

        public bool IsCreate => true;

        public IContentReader Reader => throw new NotImplementedException();
    }

    private sealed class LegacyHook(string type) : IContentLifecycleHook
    {
        public string ContentType => type;

#pragma warning disable CS0618 // the point is that an implementor written before the context still runs
        public Task<IReadOnlyList<string>> OnBeforeSaveAsync(ContentLifecycleContext context, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(context.Session is null ? [] : ["legacy hook ran with a session"]);
#pragma warning restore CS0618
    }

    private sealed class LegacyModule : IBarakoModule
    {
        public string Name => "Legacy";

        public IDocumentSession? SeededWith { get; private set; }

#pragma warning disable CS0618
        public Task SeedAsync(IDocumentSession session, IServiceProvider services, CancellationToken ct)
        {
            SeededWith = session;
            return Task.CompletedTask;
        }
#pragma warning restore CS0618
    }
}
