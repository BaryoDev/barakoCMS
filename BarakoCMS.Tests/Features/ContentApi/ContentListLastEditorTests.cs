using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;

namespace BarakoCMS.Tests;

/// <summary>
/// Each item of GET /api/contents names the last editor. The name is given only for a member of the
/// caller's tenant or a service principal of it, and never to a caller on a read-only API key.
/// </summary>
[Collection("Integration1")]
public class ContentListLastEditorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static int _ip;

    private readonly IntegrationTestFixture _factory;
    private readonly CapturingMartenLogger _logger = new();

    public ContentListLastEditorTests(IntegrationTestFixture factory) => _factory = factory;

    private static string NextIp()
    {
        var n = Interlocked.Increment(ref _ip);
        return $"198.19.{100 + n / 250 % 100}.{n % 250 + 1}"; // benchmark range, never a real caller
    }

    private async Task<string> TenantAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var slug = $"le-{Guid.NewGuid():N}"[..14];
        session.Store(new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = slug });
        await session.SaveChangesAsync(Ct);
        return slug;
    }

    private async Task<User> UserAsync(string? displayName, string? tenant, bool store = true)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = $"le-{Guid.NewGuid():N}"[..14],
            Email = $"le-{Guid.NewGuid():N}@example.com",
            DisplayName = displayName,
        };
        if (!store)
        {
            return user;
        }

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(user);
        if (tenant is not null)
        {
            session.Store(new Membership { Id = Guid.NewGuid(), UserId = user.Id, TenantSlug = tenant, Status = MembershipStatus.Active });
        }

        await session.SaveChangesAsync(Ct);
        return user;
    }

    private async Task<string> TypeAsync(string? tenant, Role? readRole = null)
    {
        var name = "le" + Guid.NewGuid().ToString("n")[..10];
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        await using var session = tenant is null ? store.LightweightSession() : store.LightweightSession(tenant);
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = name,
            DisplayName = name,
            Fields = [new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string", Sensitivity = SensitivityLevel.Public }],
        });
        await session.SaveChangesAsync(Ct);
        return name;
    }

    private Task<List<Guid>> EntriesAsync(string? tenant, string type, params Guid[] editors) =>
        EntriesAsync(tenant, type, SensitivityLevel.Public, editors);

    private async Task<List<Guid>> EntriesAsync(string? tenant, string type, SensitivityLevel sensitivity, params Guid[] editors)
    {
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        await using var session = tenant is null ? store.LightweightSession() : store.LightweightSession(tenant);
        var ids = new List<Guid>();
        foreach (var editor in editors)
        {
            var id = Guid.NewGuid();
            ids.Add(id);
            session.Store(new Content
            {
                Id = id,
                ContentType = type,
                LastModifiedBy = editor,
                Status = ContentStatus.Published,
                Sensitivity = sensitivity,
                Data = new Dictionary<string, object> { ["Title"] = "T" },
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
        }

        await session.SaveChangesAsync(Ct);
        return ids;
    }

    /// <summary>A SuperAdmin member of the tenant, or a stored SuperAdmin in the default tenant.</summary>
    private async Task<HttpClient> AdminAsync(string? tenant)
    {
        var admin = await UserAsync(null, tenant);
        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            admin.RoleIds = [SystemRoles.SuperAdminRoleId];
            session.Store(admin);
            if (tenant is not null)
            {
                var membership = await session.Query<Membership>().FirstAsync(m => m.UserId == admin.Id, Ct);
                membership.RoleIds = [SystemRoles.SuperAdminRoleId];
                session.Store(membership);
            }

            await session.SaveChangesAsync(Ct);
        }

        var claims = tenant is null ? null : new Dictionary<string, string> { ["tenant"] = tenant };
        return Client(_factory, _factory.CreateToken(["SuperAdmin"], admin.Id.ToString(), claims), tenant);
    }

    private static HttpClient Client(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory, string bearer, string? tenant)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (tenant is not null)
        {
            client.DefaultRequestHeaders.Add("X-Tenant", tenant);
        }
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, NextIp());
        return client;
    }

    private static async Task<List<JsonElement>> ListAsync(HttpClient client, string type, int pageSize = 100)
    {
        var res = await client.GetAsync($"/api/contents?contentType={type}&pageSize={pageSize}", Ct);
        var body = await res.Content.ReadAsStringAsync(Ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement.GetProperty("items").EnumerateArray().Select(e => e.Clone()).ToList();
    }

    private static JsonElement Item(List<JsonElement> items, Guid id) =>
        items.Single(i => i.GetProperty("id").GetGuid() == id);

    private static string? Name(JsonElement item) =>
        item.GetProperty("lastModifiedByName") is { ValueKind: not JsonValueKind.Null } n ? n.GetString() : null;

    private static Guid? EditorId(JsonElement item) =>
        item.GetProperty("lastModifiedBy") is { ValueKind: not JsonValueKind.Null } n ? n.GetGuid() : null;

    [Fact]
    public async Task A_list_item_carries_the_id_and_display_name_of_the_user_who_last_saved_it()
    {
        var editor = await UserAsync("Maria Santos", tenant: null);
        var plain = await UserAsync(null, tenant: null);
        var type = await TypeAsync(null);
        var ids = await EntriesAsync(null, type, editor.Id, plain.Id);

        var items = await ListAsync(await AdminAsync(null), type);

        EditorId(Item(items, ids[0])).Should().Be(editor.Id);
        Name(Item(items, ids[0])).Should().Be("Maria Santos");
        EditorId(Item(items, ids[1])).Should().Be(plain.Id);
        Name(Item(items, ids[1])).Should().Be(plain.Username, "the username stands in when no display name is set");
    }

    [Fact]
    public async Task An_item_last_saved_by_a_deleted_user_gives_the_id_and_a_null_name()
    {
        var gone = await UserAsync("Gone", tenant: null, store: false);
        var type = await TypeAsync(null);
        var ids = await EntriesAsync(null, type, gone.Id);

        var items = await ListAsync(await AdminAsync(null), type);

        EditorId(Item(items, ids[0])).Should().Be(gone.Id);
        Name(Item(items, ids[0])).Should().BeNull();
    }

    [Fact]
    public async Task An_item_last_saved_by_a_member_of_another_tenant_gives_a_null_name()
    {
        var mine = await TenantAsync();
        var other = await TenantAsync();
        var colleague = await UserAsync("Colleague", mine);
        var outsider = await UserAsync("Outsider", other);
        var type = await TypeAsync(mine);
        var ids = await EntriesAsync(mine, type, colleague.Id, outsider.Id);

        var items = await ListAsync(await AdminAsync(mine), type);

        Name(Item(items, ids[0])).Should().Be("Colleague");
        EditorId(Item(items, ids[1])).Should().Be(outsider.Id);
        Name(Item(items, ids[1])).Should().BeNull("the user has no membership in the caller's tenant");
    }

    [Fact]
    public async Task An_item_last_saved_through_a_service_principal_key_gives_the_principals_name()
    {
        var tenant = await TenantAsync();
        var otherTenant = await TenantAsync();
        var principal = new ServicePrincipal { Id = Guid.NewGuid(), TenantSlug = tenant, Name = "nightly-import" };
        var foreign = new ServicePrincipal { Id = Guid.NewGuid(), TenantSlug = otherTenant, Name = "foreign-bot" };
        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(principal, foreign);
            await session.SaveChangesAsync(Ct);
        }

        var type = await TypeAsync(tenant);
        var ids = await EntriesAsync(tenant, type, principal.Id, foreign.Id);

        var items = await ListAsync(await AdminAsync(tenant), type);

        EditorId(Item(items, ids[0])).Should().Be(principal.Id);
        Name(Item(items, ids[0])).Should().Be("nightly-import");
        Name(Item(items, ids[1])).Should().BeNull("a principal of another tenant is not named");
    }

    [Fact]
    public async Task A_page_of_100_items_issues_one_users_query()
    {
        var derived = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                var notices = services.Where(d =>
                    d.ImplementationType == typeof(barakoCMS.Infrastructure.Services.StoredValidationRulesNotice)
                    || d.ImplementationType == typeof(barakoCMS.Infrastructure.Services.StoredReferenceConditionsNotice)).ToList();
                if (notices.Count != 2)
                {
                    throw new InvalidOperationException("The startup notices are no longer registered the way this test expects.");
                }

                foreach (var notice in notices)
                {
                    services.Remove(notice);
                }
            });
            builder.ConfigureServices(services => services.ConfigureMarten(options => options.Logger(_logger)));
        });
        var client = Client(derived, await _factory.StoredUserTokenAsync("Admin", "SuperAdmin"), null);

        var editors = new List<Guid>();
        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            for (var i = 0; i < 100; i++)
            {
                var user = new User { Id = Guid.NewGuid(), Username = $"le-{Guid.NewGuid():N}"[..14], Email = $"le-{Guid.NewGuid():N}@example.com" };
                session.Store(user);
                editors.Add(user.Id);
            }

            await session.SaveChangesAsync(Ct);
        }

        var named = await TypeAsync(null);
        await EntriesAsync(null, named, editors.ToArray());
        var unnamed = await TypeAsync(null);
        await EntriesAsync(null, unnamed, Enumerable.Repeat(Guid.Empty, 100).ToArray());

        async Task<int> UsersQueriesAsync(string type)
        {
            _logger.Commands.Clear();
            (await ListAsync(client, type)).Should().HaveCount(100);
            return _logger.Commands.Count(c => c.Sql.Contains("mt_doc_users", StringComparison.OrdinalIgnoreCase));
        }

        var without = await UsersQueriesAsync(unnamed);
        var seenWithout = _logger.Commands.Count;
        var with = await UsersQueriesAsync(named);
        var seenWith = _logger.Commands.Count;

        (with - without).Should().Be(1,
            $"the names of the whole page come from one query, not one per row (users {without}/{with}, commands {seenWithout}/{seenWith})");
    }

    [Fact]
    public async Task A_read_only_API_key_caller_gets_the_id_and_a_null_name()
    {
        var editor = await UserAsync("Maria Santos", tenant: null);
        var type = await TypeAsync(null);
        var ids = await EntriesAsync(null, type, editor.Id);

        var owner = await UserAsync(null, tenant: null);
        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            owner.RoleIds = [SystemRoles.SuperAdminRoleId];
            session.Store(owner);
            await session.SaveChangesAsync(Ct);
        }

        var secret = "bcms_ro_" + Guid.NewGuid().ToString("N");
        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new ApiKey
            {
                Id = Guid.NewGuid(),
                Name = "le-ro",
                KeyHash = ApiKeyService.Hash(secret),
                Prefix = secret[..12],
                UserId = owner.Id,
                TenantSlug = Tenant.DefaultSlug,
                Scopes = ["content:read"],
                ReadOnly = true,
            });
            await session.SaveChangesAsync(Ct);
        }

        var items = await ListAsync(Client(_factory, secret, null), type);

        EditorId(Item(items, ids[0])).Should().Be(editor.Id);
        Name(Item(items, ids[0])).Should().BeNull();
    }

    [Fact]
    public async Task A_caller_holding_only_a_read_rule_gets_the_name()
    {
        var editor = await UserAsync("Maria Santos", tenant: null);
        var type = await TypeAsync(null);
        var ids = await EntriesAsync(null, type, editor.Id);

        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = $"le-viewer-{Guid.NewGuid():N}"[..20],
            Permissions = [new ContentTypePermission { ContentTypeSlug = type, Read = new PermissionRule { Enabled = true } }],
        };
        var viewer = await UserAsync(null, tenant: null, store: false);
        viewer.RoleIds = [role.Id];
        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(role);
            session.Store(viewer);
            await session.SaveChangesAsync(Ct);
        }

        var client = Client(_factory, _factory.CreateToken([role.Name], viewer.Id.ToString()), null);
        var items = await ListAsync(client, type);

        Name(Item(items, ids[0])).Should().Be("Maria Santos");
    }

    [Fact]
    public async Task A_hidden_row_has_no_editor_while_a_normal_row_on_the_page_is_named()
    {
        var editor = await UserAsync("Maria Santos", tenant: null);
        var type = await TypeAsync(null);
        var shown = (await EntriesAsync(null, type, editor.Id))[0];
        var hidden = (await EntriesAsync(null, type, SensitivityLevel.Hidden, editor.Id))[0];

        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = $"le-reader-{Guid.NewGuid():N}"[..20],
            Permissions = [new ContentTypePermission { ContentTypeSlug = type, Read = new PermissionRule { Enabled = true } }],
        };
        var reader = await UserAsync(null, tenant: null, store: false);
        reader.RoleIds = [role.Id];
        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(role);
            session.Store(reader);
            await session.SaveChangesAsync(Ct);
        }

        var items = await ListAsync(Client(_factory, _factory.CreateToken([role.Name], reader.Id.ToString()), null), type);

        Item(items, hidden).GetProperty("contentType").GetString().Should().Be("HIDDEN");
        EditorId(Item(items, hidden)).Should().BeNull();
        Name(Item(items, hidden)).Should().BeNull();
        Name(Item(items, shown)).Should().Be("Maria Santos");
    }

    [Fact]
    public async Task An_editor_whose_membership_was_removed_gives_the_id_and_a_null_name()
    {
        var tenant = await TenantAsync();
        var gone = await UserAsync("Removed Member", tenant);
        var suspended = await UserAsync("Suspended Member", tenant);
        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            foreach (var (user, status) in new[] { (gone, MembershipStatus.Removed), (suspended, MembershipStatus.Suspended) })
            {
                var membership = await session.Query<Membership>().FirstAsync(m => m.UserId == user.Id && m.TenantSlug == tenant, Ct);
                membership.Status = status;
                session.Store(membership);
            }

            await session.SaveChangesAsync(Ct);
        }

        var type = await TypeAsync(tenant);
        var ids = await EntriesAsync(tenant, type, gone.Id, suspended.Id);

        var items = await ListAsync(await AdminAsync(tenant), type);

        EditorId(Item(items, ids[0])).Should().Be(gone.Id);
        Name(Item(items, ids[0])).Should().BeNull();
        Name(Item(items, ids[1])).Should().Be("Suspended Member", "a suspended member is named like the members roster does");
    }

    [Fact]
    public async Task An_entry_saved_with_an_empty_editor_id_has_a_null_last_modified_by()
    {
        var type = await TypeAsync(null);
        var ids = await EntriesAsync(null, type, Guid.Empty);

        var items = await ListAsync(await AdminAsync(null), type);

        EditorId(Item(items, ids[0])).Should().BeNull();
        Name(Item(items, ids[0])).Should().BeNull();
    }
}
