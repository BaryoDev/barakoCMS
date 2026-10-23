using System.Net;
using System.Net.Http.Json;
using barakoCMS.Extensions;
using barakoCMS.Models;
using barakoCMS.Modules;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// <see cref="ContentPermissionDefaults"/>: a module that seeds a content type says which seeded
/// roles may use it, so a role needs no hand-added permission rule (issue #1162).
/// </summary>
/// <remarks>
/// Before this, a seeded type was creatable by SuperAdmin only. Every role here is stored under a
/// random id and name, so a grant that reached the wrong role shows up on a role this class made.
/// </remarks>
[Collection("Sequential")]
public class ModuleContentPermissionDefaultsTests
{
    private readonly IntegrationTestFixture _factory;

    public ModuleContentPermissionDefaultsTests(IntegrationTestFixture factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class SeedingModule(string slug, SeededRole role, bool declares) : IBarakoModule
    {
        public string Name => "ContentPermissionTest";

        public ContentPermissionDefaults? ContentPermissions => declares
            ? ContentPermissionDefaults.None().Allow(slug, role, ContentAction.Create, ContentAction.Read)
            : null;

        public async Task SeedAsync(IDocumentSession session, IServiceProvider services, CancellationToken ct)
        {
            if (await session.Query<ContentTypeDefinition>().AnyAsync(t => t.Name == slug, ct))
            {
                return;
            }

            session.Store(new ContentTypeDefinition
            {
                Id = Guid.NewGuid(),
                Name = slug,
                DisplayName = "Seeded",
                Fields = [new FieldDefinition { Name = "Title", Type = "string" }],
            });
        }
    }

    private static string NewSlug() => "seeded" + Guid.NewGuid().ToString("n")[..8];

    private async Task<Role> StoreRoleAsync(params ContentTypePermission[] permissions)
    {
        var role = new Role { Id = Guid.NewGuid(), Name = $"Role_{Guid.NewGuid():n}", Permissions = [.. permissions] };
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(role);
        await session.SaveChangesAsync(Ct);
        return role;
    }

    private async Task<Role> LoadAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IQuerySession>().LoadAsync<Role>(id, Ct))!;
    }

    private async Task SeedAsync(IBarakoModule module)
    {
        var services = new ServiceCollection();
        services.AddSingleton(module);

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        services.AddScoped(_ => session);
        // The resolver the HTTP calls use, so the seeder's invalidation reaches the cached denial.
        services.AddScoped(_ => scope.ServiceProvider.GetRequiredService<barakoCMS.Infrastructure.Services.IPermissionResolver>());

        await using var provider = services.BuildServiceProvider();
        await provider.RunBarakoModuleSeedersAsync(Ct);
    }

    private async Task<HttpClient> ClientHoldingAsync(Role role)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = $"cp_{Guid.NewGuid():n}",
            Email = $"cp_{Guid.NewGuid():n}@example.com",
            RoleIds = [role.Id],
        };
        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(user);
            await session.SaveChangesAsync(Ct);
        }

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(roles: [role.Name], userId: user.Id.ToString()));
        return client;
    }

    private static Task<HttpResponseMessage> CreateEntryAsync(HttpClient client, string slug) =>
        client.PostAsJsonAsync("/api/contents", new
        {
            contentType = slug,
            data = new Dictionary<string, object> { ["Title"] = "hello" },
        }, Ct);

    [Fact]
    public async Task A_seeded_type_is_creatable_by_the_role_the_module_declared()
    {
        var slug = NewSlug();
        var role = await StoreRoleAsync();
        var client = await ClientHoldingAsync(role);

        (await CreateEntryAsync(client, slug)).StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "with no rule a role cannot use the type, which is the behaviour being fixed");

        await SeedAsync(new SeedingModule(slug, new SeededRole(role.Id, role.Name), declares: true));

        var created = await CreateEntryAsync(client, slug);
        created.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", created.StatusCode, await created.Content.ReadAsStringAsync(Ct));
        var stored = (await LoadAsync(role.Id)).Permissions;
        stored.Should().ContainSingle(p => p.ContentTypeSlug == slug);
        var entry = stored.Single(p => p.ContentTypeSlug == slug);
        entry.Create.Enabled.Should().BeTrue();
        entry.Read.Enabled.Should().BeTrue();
        entry.Update.Enabled.Should().BeFalse("only the declared actions are granted");
        entry.Delete.Enabled.Should().BeFalse();
    }

    [Fact]
    public async Task A_role_the_module_did_not_name_gets_nothing()
    {
        var slug = NewSlug();
        var named = await StoreRoleAsync();
        var other = await StoreRoleAsync();

        await SeedAsync(new SeedingModule(slug, new SeededRole(named.Id, named.Name), declares: true));

        (await LoadAsync(named.Id)).Permissions.Should().HaveCount(1);
        (await LoadAsync(other.Id)).Permissions.Should().BeEmpty();
    }

    [Fact]
    public async Task A_second_seed_does_not_overwrite_what_an_admin_changed()
    {
        var slug = NewSlug();
        var role = await StoreRoleAsync();
        var module = new SeedingModule(slug, new SeededRole(role.Id, role.Name), declares: true);

        await SeedAsync(module);

        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var edited = (await session.LoadAsync<Role>(role.Id, Ct))!;
            var entry = edited.Permissions.Single(p => p.ContentTypeSlug == slug);
            entry.Create.Enabled = false;
            entry.Delete.Enabled = true;
            session.Store(edited);
            await session.SaveChangesAsync(Ct);
        }

        await SeedAsync(module);

        var after = (await LoadAsync(role.Id)).Permissions;
        after.Should().ContainSingle(p => p.ContentTypeSlug == slug, "a second seed adds no second entry");
        var kept = after.Single(p => p.ContentTypeSlug == slug);
        kept.Create.Enabled.Should().BeFalse("the admin switched it off");
        kept.Delete.Enabled.Should().BeTrue("the admin switched it on");
    }

    [Fact]
    public async Task A_role_that_already_has_an_entry_for_the_type_keeps_it_untouched()
    {
        var slug = NewSlug();
        var role = await StoreRoleAsync(new ContentTypePermission
        {
            ContentTypeSlug = slug,
            Update = new PermissionRule { Enabled = true },
        });

        await SeedAsync(new SeedingModule(slug, new SeededRole(role.Id, role.Name), declares: true));

        var stored = (await LoadAsync(role.Id)).Permissions;
        stored.Should().HaveCount(1);
        stored[0].Update.Enabled.Should().BeTrue();
        stored[0].Create.Enabled.Should().BeFalse("the existing entry was not merged with the default");
    }

    [Fact]
    public async Task A_declaration_for_a_type_that_does_not_exist_is_skipped_and_reported()
    {
        var role = await StoreRoleAsync();
        var skipped = new List<string>();
        var defaults = ContentPermissionDefaults.None()
            .Allow(NewSlug(), new SeededRole(role.Id, role.Name), ContentAction.Read);

        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            (await defaults.GrantAsync(session, Ct, skipped.Add)).Should().Be(0);
            await session.SaveChangesAsync(Ct);
        }

        skipped.Should().ContainSingle();
        (await LoadAsync(role.Id)).Permissions.Should().BeEmpty();
    }

    [Fact]
    public async Task A_role_deployed_under_another_tenant_is_not_granted_by_name()
    {
        var slug = NewSlug();
        var name = $"Role_{Guid.NewGuid():n}";
        var tenantsOwn = new Role { Id = Guid.NewGuid(), Name = name, TenantSlug = "some-other-tenant" };
        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(tenantsOwn);
            await session.SaveChangesAsync(Ct);
        }

        await SeedAsync(new SeedingModule(slug, new SeededRole(Guid.NewGuid(), name), declares: true));

        (await LoadAsync(tenantsOwn.Id)).Permissions.Should().BeEmpty();
    }

    [Fact]
    public async Task A_module_with_no_declaration_grants_nothing_as_before()
    {
        var slug = NewSlug();
        var role = await StoreRoleAsync();

        await SeedAsync(new SeedingModule(slug, new SeededRole(role.Id, role.Name), declares: false));

        (await LoadAsync(role.Id)).Permissions.Should().BeEmpty();
        var client = await ClientHoldingAsync(role);
        (await CreateEntryAsync(client, slug)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public void A_module_that_does_not_override_the_member_declares_nothing()
    {
        IBarakoModule module = new BareModule();

        module.ContentPermissions.Should().BeNull();
    }

    private sealed class BareModule : IBarakoModule
    {
        public string Name => "Bare";
    }

    [Fact]
    public void A_declaration_with_no_action_or_an_empty_slug_is_refused()
    {
        var role = new SeededRole(Guid.NewGuid(), "R");

        var noAction = () => ContentPermissionDefaults.None().Allow("t", role);
        var noSlug = () => ContentPermissionDefaults.None().Allow(" ", role, ContentAction.Read);

        noAction.Should().Throw<ArgumentException>();
        noSlug.Should().Throw<ArgumentException>();
    }
}
