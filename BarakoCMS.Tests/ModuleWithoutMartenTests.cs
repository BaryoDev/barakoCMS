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

        foreach (var type in new[] { typeof(NoMartenProbeModule), typeof(NoMartenProbeHook) })
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
        Path.Combine(ComposeDefaultsTests.RepoRoot(), "BarakoCMS.Tests", "NoMartenProbe.cs");

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
