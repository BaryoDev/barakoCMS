using System.Reflection;
using barakoCMS.Models;
using barakoCMS.Modules;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// <see cref="CapabilityDefaults"/>: a module says once which seeded roles start with its
/// capabilities, by seeded role id, and its seed grants from that (issue #886).
/// </summary>
/// <remarks>
/// Every role here is stored under a random id and a random name, so a grant that reached the wrong
/// role shows up on a role this class made and not on the shared Admin.
/// </remarks>
[Collection("Sequential")]
public class ModuleCapabilityDefaultsTests
{
    private readonly IntegrationTestFixture _factory;

    public ModuleCapabilityDefaultsTests(IntegrationTestFixture factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_role_holding_the_seeded_id_is_granted_whatever_it_is_called_now()
    {
        var seededName = NewName("Seeded");
        var renamed = await StoreRoleAsync(NewName("Renamed"), "held_before");

        var changed = await GrantAsync(
            CapabilityDefaults.For("cap_one", "cap_two").GrantedTo(new SeededRole(renamed.Id, seededName)));

        changed.Should().Be(1);
        var stored = await LoadAsync(renamed.Id);
        stored!.Name.Should().Be(renamed.Name, "a grant changes capabilities and nothing else");
        stored.SystemCapabilities.Should().BeEquivalentTo(["held_before", "cap_one", "cap_two"]);
        (await LoadByNameAsync(seededName)).Should().BeNull("no role was created under the seeded name");
    }

    [Fact]
    public async Task Where_no_role_holds_the_id_the_role_carrying_the_seeded_name_is_granted()
    {
        var byName = await StoreRoleAsync(NewName("Older Database"));
        var seededId = Guid.NewGuid();

        var changed = await GrantAsync(
            CapabilityDefaults.For("cap_one").GrantedTo(new SeededRole(seededId, byName.Name)));

        changed.Should().Be(1);
        (await LoadAsync(byName.Id))!.SystemCapabilities.Should().Equal("cap_one");
        (await LoadAsync(seededId)).Should().BeNull("the seeded id was not taken by a new role");
    }

    /// <summary>
    /// The id decides. A role an operator made, which now carries the seeded name because the seeded
    /// role was renamed away from it, is theirs to grant and is not touched.
    /// </summary>
    [Fact]
    public async Task A_role_that_only_shares_the_seeded_name_is_left_alone_when_another_holds_the_id()
    {
        var seeded = await StoreRoleAsync(NewName("Renamed"));
        var operatorsOwn = await StoreRoleAsync(NewName("Took The Name"));

        var changed = await GrantAsync(
            CapabilityDefaults.For("cap_one").GrantedTo(new SeededRole(seeded.Id, operatorsOwn.Name)));

        changed.Should().Be(1);
        (await LoadAsync(seeded.Id))!.SystemCapabilities.Should().Equal("cap_one");
        (await LoadAsync(operatorsOwn.Id))!.SystemCapabilities.Should().BeEmpty();
    }

    /// <summary>
    /// A role the host never seeded is skipped, not created, under the id or under the name.
    /// </summary>
    [Fact]
    public async Task A_role_that_does_not_exist_is_skipped_rather_than_created()
    {
        var missing = new SeededRole(Guid.NewGuid(), NewName("Never Seeded"));
        var present = await StoreRoleAsync(NewName("Present"));

        var changed = await GrantAsync(
            CapabilityDefaults.For("cap_one").GrantedTo(missing, new SeededRole(present.Id, present.Name)));

        changed.Should().Be(1, "the role that exists is granted, so the run did something");
        (await LoadAsync(missing.Id)).Should().BeNull();
        (await LoadByNameAsync(missing.Name)).Should().BeNull();
    }

    /// <summary>
    /// A second run writes nothing, and a capability an operator gave the role is still there.
    /// </summary>
    [Fact]
    public async Task Running_it_twice_changes_nothing_the_second_time_and_takes_nothing_off()
    {
        var role = await StoreRoleAsync(NewName("Twice"), "operator_granted");
        var defaults = CapabilityDefaults.For("cap_one").GrantedTo(new SeededRole(role.Id, role.Name));

        (await GrantAsync(defaults)).Should().Be(1, "the first run has something to do, or the second proves nothing");
        (await GrantAsync(defaults)).Should().Be(0);

        var stored = (await LoadAsync(role.Id))!.SystemCapabilities;
        stored.Should().HaveCount(2);
        stored.Should().BeEquivalentTo(["operator_granted", "cap_one"]);
    }

    /// <summary>
    /// What grants by name did, kept: the grant runs on every start and unions, so a default taken
    /// off a seeded role is back after the next one.
    /// </summary>
    [Fact]
    public async Task A_default_taken_off_a_seeded_role_comes_back_on_the_next_run()
    {
        var role = await StoreRoleAsync(NewName("Taken Off"));
        var defaults = CapabilityDefaults.For("cap_one", "cap_two").GrantedTo(new SeededRole(role.Id, role.Name));
        (await GrantAsync(defaults)).Should().Be(1);

        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var stored = await session.LoadAsync<Role>(role.Id, Ct);
            stored!.SystemCapabilities = ["cap_two"];
            session.Store(stored);
            await session.SaveChangesAsync(Ct);
        }

        (await GrantAsync(defaults)).Should().Be(1);
        (await LoadAsync(role.Id))!.SystemCapabilities.Should().BeEquivalentTo(["cap_one", "cap_two"]);
    }

    [Fact]
    public void The_legacy_list_is_each_roles_seeded_name_and_then_SuperAdmin()
    {
        var accountant = new SeededRole(Guid.NewGuid(), "Accountant");

        CapabilityDefaults.For("cap_one").GrantedTo(SystemRoles.Admin).LegacyRoles
            .Should().Equal("Admin", "SuperAdmin");
        CapabilityDefaults.For("cap_one").GrantedTo(accountant, SystemRoles.Admin).LegacyRoles
            .Should().Equal("Accountant", "Admin", "SuperAdmin");
        CapabilityDefaults.For("cap_one").LegacyRoles
            .Should().Equal("SuperAdmin");

        SystemRoles.Admin.Id.Should().Be(SystemRoles.AdminRoleId);
        SystemRoles.Admin.Name.Should().Be("Admin");
    }

    [Fact]
    public void A_declaration_refuses_an_empty_capability_and_a_role_without_its_id_or_name()
    {
        var blank = () => CapabilityDefaults.For("cap_one", " ");
        var noId = () => CapabilityDefaults.For("cap_one").GrantedTo(new SeededRole(Guid.Empty, "Admin"));
        var noName = () => CapabilityDefaults.For("cap_one").GrantedTo(new SeededRole(Guid.NewGuid(), ""));

        blank.Should().Throw<ArgumentException>();
        noId.Should().Throw<ArgumentException>();
        noName.Should().Throw<ArgumentException>();
    }

    /// <summary>
    /// Every first-party module declares its defaults once: the declaration covers each capability
    /// the class names, goes to the seeded Admin role by its id, and is where the legacy list comes
    /// from. No class keeps a list of role names to seed.
    /// </summary>
    [Fact]
    public void Every_first_party_module_declares_its_defaults_once_by_seeded_role_id()
    {
        // Named so their assemblies are loaded before the scan, and so the scan has a floor.
        Type[] known =
        [
            typeof(BarakoCMS.AI.AiCapabilities),
            typeof(BarakoCMS.Accounting.AccountingCapabilities),
            typeof(BarakoCMS.Analytics.Umami.AnalyticsCapabilities),
            typeof(BarakoCMS.Diagnostics.DiagnosticsCapabilities),
            typeof(BarakoCMS.Email.Resend.ResendEmailCapabilities),
            typeof(BarakoCMS.FeatureFlags.FeatureFlagCapabilities),
            typeof(BarakoCMS.Files.FileCapabilities),
            typeof(BarakoCMS.Forms.FormsCapabilities),
            typeof(BarakoCMS.Import.ImportCapabilities),
            typeof(BarakoCMS.Portability.PortabilityCapabilities),
            typeof(BarakoCMS.Pwa.PwaCapabilities),
        ];

        // Scanned as well as named, so a module added later is held to the same rule.
        var classes = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.GetName().Name?.StartsWith("BarakoCMS.", StringComparison.Ordinal) == true)
            .Where(a => a != typeof(CapabilityDefaults).Assembly && a != typeof(ModuleCapabilityDefaultsTests).Assembly)
            .SelectMany(LoadableTypes.In)
            .Where(t => t is { IsAbstract: true, IsSealed: true }
                     && t.Name.EndsWith("Capabilities", StringComparison.Ordinal)
                     && Constants(t).Count > 0)
            .ToList();

        classes.Should().HaveCountGreaterThanOrEqualTo(known.Length);
        classes.Should().Contain(known);

        const BindingFlags statics = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        foreach (var type in classes)
        {
            var defaults = type.GetField("Defaults", statics)?.GetValue(null) as CapabilityDefaults;
            defaults.Should().NotBeNull($"{type.Name} declares its defaults as a CapabilityDefaults named Defaults");

            var declared = Constants(type);
            defaults!.Capabilities.Should().HaveCount(declared.Count, $"{type.Name} grants every capability it names");
            defaults.Capabilities.Should().BeEquivalentTo(declared);

            defaults.Roles.Should().NotBeEmpty($"{type.Name} grants to a seeded role");
            defaults.Roles.Should().Contain(r => r.Id == SystemRoles.AdminRoleId && r.Name == "Admin",
                $"{type.Name} reached Admin before, and Admin is named by its seeded id");
            defaults.Roles.Should().OnlyContain(r => r.Id != Guid.Empty);

            type.GetField("SeededRoles", statics).Should().BeNull($"{type.Name} keeps no list of role names to seed");

            if (type.GetField("LegacyRoles", statics) is { } legacy)
            {
                legacy.GetCustomAttribute<ObsoleteAttribute>().Should().NotBeNull(
                    $"{type.Name}.LegacyRoles is kept for callers outside the module and marked for removal");
                ((string[])legacy.GetValue(null)!).Should().Equal(defaults.LegacyRoles,
                    $"{type.Name}.LegacyRoles comes from the declaration, not from a second list");
            }
        }
    }

    /// <summary>
    /// The Accounting module seeds a role of its own. Renamed, it is found by its id: the seed does
    /// not store a new Accountant over it, which would discard the name and every permission an
    /// operator gave it.
    /// </summary>
    [Fact]
    public async Task The_accounting_seed_keeps_an_accountant_role_that_was_renamed()
    {
        var id = BarakoCMS.Accounting.AccountingModule.AccountantRoleId;
        var original = await LoadAsync(id);
        original.Should().NotBeNull("the fixture seeds the Accounting module, which creates the role");

        var renamed = NewName("Bookkeeper");

        try
        {
            using (var scope = _factory.Services.CreateScope())
            {
                var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
                var role = await session.LoadAsync<Role>(id, Ct);
                role!.Name = renamed;

                // Held already, so the grant below has nothing to write and the only thing the
                // seed could store is a role of its own.
                role.SystemCapabilities =
                [
                    BarakoCMS.Accounting.AccountingCapabilities.ViewLedger,
                    BarakoCMS.Accounting.AccountingCapabilities.PostEntries,
                ];
                role.Permissions =
                [
                    new ContentTypePermission { ContentTypeSlug = "renamed-accountant-marker", Read = new PermissionRule { Enabled = true } },
                ];
                session.Store(role);
                await session.SaveChangesAsync(Ct);
            }

            using (var scope = _factory.Services.CreateScope())
            {
                var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
                await new BarakoCMS.Accounting.AccountingModule().SeedAsync(session, scope.ServiceProvider, Ct);
                await session.SaveChangesAsync(Ct);
            }

            var after = await LoadAsync(id);
            after.Should().NotBeNull();
            after!.Name.Should().Be(renamed);
            after.Permissions.Should().ContainSingle()
                .Which.ContentTypeSlug.Should().Be("renamed-accountant-marker");
            after.SystemCapabilities.Should().HaveCount(2);
            (await LoadByNameAsync("Accountant")).Should().BeNull("no second role was made under the seeded name");
        }
        finally
        {
            using var scope = _factory.Services.CreateScope();
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(original!);
            await session.SaveChangesAsync(CancellationToken.None);
        }
    }

    private static List<string> Constants(Type type) =>
        type.GetFields(BindingFlags.Static | BindingFlags.Public)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

    private static string NewName(string prefix) => $"{prefix} {Guid.NewGuid():N}";

    private async Task<int> GrantAsync(CapabilityDefaults defaults)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var changed = await defaults.GrantAsync(session, Ct);
        await session.SaveChangesAsync(Ct);
        return changed;
    }

    private async Task<Role> StoreRoleAsync(string name, params string[] capabilities)
    {
        var role = new Role { Id = Guid.NewGuid(), Name = name, SystemCapabilities = capabilities.ToList() };

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(role);
        await session.SaveChangesAsync(Ct);
        return role;
    }

    private async Task<Role?> LoadAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IQuerySession>().LoadAsync<Role>(id, Ct);
    }

    private async Task<Role?> LoadByNameAsync(string name)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IQuerySession>()
            .Query<Role>().FirstOrDefaultAsync(r => r.Name == name, Ct);
    }
}
