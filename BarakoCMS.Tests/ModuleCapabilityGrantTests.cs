using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Models;
using barakoCMS.Modules;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// <see cref="ModuleCapabilities.GrantAsync"/>, which is how a module's capabilities reach the roles
/// that already reached its endpoints.
/// </summary>
[Collection("Sequential")]
public class ModuleCapabilityGrantTests
{
    private readonly IntegrationTestFixture _factory;

    public ModuleCapabilityGrantTests(IntegrationTestFixture factory) => _factory = factory;

    [Fact]
    public async Task It_adds_the_capabilities_to_a_role_that_exists()
    {
        var name = await SeedRoleAsync("cap_already_here");

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var changed = await ModuleCapabilities.GrantAsync(
            session, [name], ["cap_one", "cap_two"], TestContext.Current.CancellationToken);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        changed.Should().Be(1);
        (await LoadAsync(name)).SystemCapabilities.Should()
            .BeEquivalentTo(["cap_already_here", "cap_one", "cap_two"],
                "the names it had are kept and the module's are added");
    }

    /// <summary>
    /// A second run changes nothing, so a restart is not a write and a name is not duplicated.
    /// </summary>
    [Fact]
    public async Task Running_it_twice_changes_nothing_the_second_time()
    {
        var name = await SeedRoleAsync();

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        (await ModuleCapabilities.GrantAsync(session, [name], ["cap_one"], TestContext.Current.CancellationToken))
            .Should().Be(1, "the first run has something to do, or the second proves nothing");
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        using var second = _factory.Services.CreateScope();
        var again = second.ServiceProvider.GetRequiredService<IDocumentSession>();

        (await ModuleCapabilities.GrantAsync(again, [name], ["cap_one"], TestContext.Current.CancellationToken))
            .Should().Be(0);

        (await LoadAsync(name)).SystemCapabilities.Should().ContainSingle(c => c == "cap_one");
    }

    /// <summary>
    /// A role the host never seeded is skipped, not created.
    /// </summary>
    /// <remarks>
    /// A module does not know whether the host seeded the system roles at all. Inventing an "Admin"
    /// on a deployment that deliberately has none would be a module granting itself access to a role
    /// nobody made.
    /// </remarks>
    [Fact]
    public async Task A_role_that_does_not_exist_is_skipped_rather_than_created()
    {
        var missing = $"Never Seeded {Guid.NewGuid():N}";

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var changed = await ModuleCapabilities.GrantAsync(
            session, [missing], ["cap_one"], TestContext.Current.CancellationToken);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        changed.Should().Be(0);

        using var check = _factory.Services.CreateScope();
        var found = await check.ServiceProvider.GetRequiredService<IQuerySession>()
            .Query<Role>().FirstOrDefaultAsync(r => r.Name == missing, TestContext.Current.CancellationToken);
        found.Should().BeNull("no role was invented");
    }

    /// <summary>
    /// Every module that gates on a capability grants it at seed, so nothing is left reachable only
    /// through the legacy role-name fallback.
    /// </summary>
    /// <remarks>
    /// Read off the module instances rather than from a list here, so a module added later is
    /// covered without anybody remembering to add it. This is the "done when" of issue #443 that
    /// matters most: turning the fallback off must not take a module away from the Admin role.
    /// </remarks>
    [Fact]
    public async Task Every_module_grants_its_own_capabilities_to_admin()
    {
        var admin = $"Admin Stand In {Guid.NewGuid():N}";
        await SeedRoleAsync(roleName: admin);

        // Constructed from every module type the loaded assemblies define, rather than a list kept
        // here. A hardcoded list only covers the modules somebody remembered to add: the Import
        // module shipped a capability and a seeder, and dropping its grant left this green.
        var modules = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.GetName().Name?.StartsWith("BarakoCMS.", StringComparison.Ordinal) == true)
            .SelectMany(a =>
            {
                try { return a.GetTypes(); }
                catch (System.Reflection.ReflectionTypeLoadException e) { return e.Types.Where(t => t is not null)!; }
            })
            .Where(t => t is { IsAbstract: false, IsInterface: false }
                     && typeof(IBarakoModule).IsAssignableFrom(t)
                     && t!.GetConstructor(Type.EmptyTypes) is not null)
            .Select(t => (IBarakoModule)Activator.CreateInstance(t!)!)
            .ToList();

        modules.Should().HaveCountGreaterThan(5,
            "the suite references every first-party module, so finding almost none means this "
          + "stopped looking rather than that the modules stopped existing");

        var declared = modules
            .SelectMany(m => CapabilitiesDeclaredBy(m.GetType().Assembly))
            .Distinct()
            .ToList();

        declared.Should().NotBeEmpty("the modules declare capabilities, or this test stopped looking");

        foreach (var module in modules)
        {
            using var scope = _factory.Services.CreateScope();
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            await module.SeedAsync(session, scope.ServiceProvider, TestContext.Current.CancellationToken);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // The seeders grant to "Admin", not to the stand-in above, so read the real one. It exists
        // because the fixture's host runs the core seeder.
        var real = await LoadAsync("Admin");
        real.SystemCapabilities.Should().Contain(declared,
            "every capability a module declares reaches the role its old gate listed");
    }

    private static IEnumerable<string> CapabilitiesDeclaredBy(System.Reflection.Assembly assembly) =>
        assembly.GetTypes()
            .Where(t => t.IsAbstract && t.IsSealed && t.Name.EndsWith("Capabilities", StringComparison.Ordinal))
            .SelectMany(t => t.GetFields()
                .Where(f => f.IsLiteral && f.FieldType == typeof(string))
                .Select(f => (string)f.GetRawConstantValue()!));

    private async Task<string> SeedRoleAsync(string? startsWith = null, string? roleName = null)
    {
        var name = roleName ?? $"Grant Target {Guid.NewGuid():N}";

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new Role
        {
            Id = Guid.NewGuid(),
            Name = name,
            SystemCapabilities = startsWith is null ? [] : [startsWith],
        });
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return name;
    }

    private async Task<Role> LoadAsync(string name)
    {
        using var scope = _factory.Services.CreateScope();
        var role = await scope.ServiceProvider.GetRequiredService<IQuerySession>()
            .Query<Role>().FirstOrDefaultAsync(r => r.Name == name, TestContext.Current.CancellationToken);
        role.Should().NotBeNull("the role was seeded");
        return role!;
    }
}
