using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests;

/// <summary>
/// Approving is a different right from editing, and a person cannot move on what they raised.
/// </summary>
/// <remarks>
/// Permissions were create, read, update and delete, and a status change checked "update". So
/// whoever could edit an invoice could also approve it, and separation of duties could not be
/// expressed at all. It is the first thing an auditor asks about.
///
/// The two failure directions are opposite and the second is the one that quietly does nothing. Not
/// enforcing leaves a clerk able to approve their own work. Enforcing by falling back to the Update
/// rule for an undeclared transition looks like it works and grants approval to everyone who can
/// edit, which is the defect wearing the fix's clothes.
/// </remarks>
[Collection("Sequential")]
public class TransitionPermissionTests
{
    private readonly IntegrationTestFixture _factory;

    public TransitionPermissionTests(IntegrationTestFixture factory) => _factory = factory;

    private static LifecycleDefinition Invoice() => new()
    {
        States = ["Draft", "Submitted", "Approved"],
        InitialState = "Draft",
        Transitions =
        [
            new StateTransition { Name = "Submit", From = "Draft", To = "Submitted" },
            new StateTransition { Name = "Approve", From = "Submitted", To = "Approved" },
        ],
    };

    /// <summary>A user whose role grants exactly the permissions given, and nothing else.</summary>
    private async Task<(HttpClient Client, Guid UserId)> UserAsync(string type, ContentTypePermission permission)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = $"Role_{Guid.NewGuid():n}",
            Permissions = [permission],
        };
        session.Store(role);

        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = $"tp_{Guid.NewGuid():n}",
            Email = $"tp_{Guid.NewGuid():n}@example.com",
            RoleIds = [role.Id],
        };
        session.Store(user);
        await session.SaveChangesAsync();

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(roles: [role.Name], userId: user.Id.ToString()));
        return (client, user.Id);
    }

    private static ContentTypePermission Clerk(string type) => new()
    {
        ContentTypeSlug = type,
        Create = new PermissionRule { Enabled = true },
        Read = new PermissionRule { Enabled = true },
        Update = new PermissionRule { Enabled = true },
        // Submit only. No Approve, so an undeclared transition must refuse rather than fall through
        // to the Update rule above, which is enabled.
        Transitions = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Submit"] = new PermissionRule { Enabled = true },
        },
    };

    private static ContentTypePermission Manager(string type) => new()
    {
        ContentTypeSlug = type,
        Read = new PermissionRule { Enabled = true },
        // Deliberately no Update. A manager approves an amount they may not edit, which is the other
        // half of separation of duties and is not reachable from CRUD.
        Transitions = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Approve"] = new PermissionRule { Enabled = true },
        },
    };

    /// <summary>Creates the content type as an administrator, then hands back the slug.</summary>
    private async Task<string> TypeAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var roleIds = new List<Guid>();
        foreach (var name in new[] { "SuperAdmin", "Admin" })
        {
            var role = await session.Query<Role>().FirstOrDefaultAsync(r => r.Name == name);
            if (role is null) { role = new Role { Id = Guid.NewGuid(), Name = name }; session.Store(role); }
            roleIds.Add(role.Id);
        }

        var admin = new User
        {
            Id = Guid.NewGuid(),
            Username = $"tpadmin_{Guid.NewGuid():n}",
            Email = $"tpadmin_{Guid.NewGuid():n}@example.com",
            RoleIds = roleIds,
        };
        session.Store(admin);
        await session.SaveChangesAsync();

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(roles: ["SuperAdmin", "Admin"], userId: admin.Id.ToString()));

        var slug = "inv" + Guid.NewGuid().ToString("n")[..8];
        var res = await client.PostAsJsonAsync("/api/content-types", new
        {
            name = slug,
            displayName = "Invoice",
            fields = new[] { new { name = "Title", type = "string" } },
            lifecycle = Invoice(),
        });
        res.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", res.StatusCode, await res.Content.ReadAsStringAsync());
        return slug;
    }

    private static async Task<Guid> EntryAsync(HttpClient client, string type)
    {
        var res = await client.PostAsJsonAsync("/api/contents", new
        {
            contentType = type,
            data = new Dictionary<string, object> { ["Title"] = "an invoice" },
        });
        res.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", res.StatusCode, await res.Content.ReadAsStringAsync());
        using var doc = System.Text.Json.JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("id").GetGuid();
    }

    /// <summary>
    /// Editing does not confer approving.
    /// </summary>
    /// <remarks>
    /// The whole issue in one test. The clerk's Update rule is enabled, so a resolver that fell back
    /// to it for an undeclared transition would answer yes here and the feature would look
    /// implemented while granting exactly what it was built to prevent.
    /// </remarks>
    [Fact]
    public async Task Someone_who_may_edit_may_not_approve()
    {
        var type = await TypeAsync();
        var (clerk, _) = await UserAsync(type, Clerk(type));
        var (other, _) = await UserAsync(type, Clerk(type));

        var id = await EntryAsync(clerk, type);
        await other.PutAsJsonAsync($"/api/contents/{id}/status", new { id, transition = "Submit" });

        var res = await other.PutAsJsonAsync($"/api/contents/{id}/status", new { id, transition = "Approve" });

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "the clerk role has Update enabled and no Approve transition, and Update must not stand in for it");
    }

    /// <summary>
    /// The control. Without it, a resolver that refused every transition would pass the test above.
    /// </summary>
    [Fact]
    public async Task Someone_granted_the_transition_may_perform_it()
    {
        var type = await TypeAsync();
        var (clerk, _) = await UserAsync(type, Clerk(type));
        var (submitter, _) = await UserAsync(type, Clerk(type));
        var (manager, _) = await UserAsync(type, Manager(type));

        var id = await EntryAsync(clerk, type);
        await submitter.PutAsJsonAsync($"/api/contents/{id}/status", new { id, transition = "Submit" });

        var res = await manager.PutAsJsonAsync($"/api/contents/{id}/status", new { id, transition = "Approve" });

        res.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", res.StatusCode, await res.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// Approving does not confer editing, which is the other direction and just as load bearing.
    /// </summary>
    /// <remarks>
    /// A manager approves an amount they may not change. If a transition permission implied Update,
    /// granting approval would hand out edit rights, and the audit answer would be the wrong way
    /// round from the one above.
    /// </remarks>
    [Fact]
    public async Task Someone_who_may_approve_may_not_edit()
    {
        var type = await TypeAsync();
        var (clerk, _) = await UserAsync(type, Clerk(type));
        var (manager, _) = await UserAsync(type, Manager(type));

        var id = await EntryAsync(clerk, type);

        var res = await manager.PutAsJsonAsync($"/api/contents/{id}", new
        {
            id,
            data = new Dictionary<string, object> { ["Title"] = "changed the amount" },
        });

        res.IsSuccessStatusCode.Should().BeFalse("the manager role grants a transition and not Update");
    }

    /// <summary>
    /// The person who raised a record cannot move it on, by default.
    /// </summary>
    /// <remarks>
    /// Read from `CreatedBy`, not `LastModifiedBy`, which moves to whoever edited last and would make
    /// the check mean nothing after any edit. Refused by default because that is the direction that
    /// can be relaxed later: an approval that should not have happened cannot be undone, and taking
    /// the permission away afterwards removes something people were relying on.
    /// </remarks>
    [Fact]
    public async Task The_person_who_raised_it_cannot_move_it_on()
    {
        var type = await TypeAsync();
        var (clerk, _) = await UserAsync(type, Clerk(type));

        var id = await EntryAsync(clerk, type);

        var res = await clerk.PutAsJsonAsync($"/api/contents/{id}/status", new { id, transition = "Submit" });

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "Submit is granted to this role, so the refusal is the self transition rule and not the permission");
    }

    /// <summary>
    /// Separation of duties applies to an administrator too.
    /// </summary>
    /// <remarks>
    /// The surprising half, and the reason it is worth pinning. SuperAdmin bypasses every permission
    /// check by design, and if it bypassed this one as well then separation of duties would be
    /// decorative in exactly the deployments that have an auditor. It is a policy about who acted,
    /// not a permission, so the bypass does not apply.
    /// </remarks>
    [Fact]
    public async Task An_administrator_cannot_move_on_their_own_record_either()
    {
        var type = await TypeAsync();

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var role = await session.Query<Role>().FirstOrDefaultAsync(r => r.Name == "SuperAdmin");
        var admin = new User
        {
            Id = Guid.NewGuid(),
            Username = $"tpsa_{Guid.NewGuid():n}",
            Email = $"tpsa_{Guid.NewGuid():n}@example.com",
            RoleIds = [role!.Id],
        };
        session.Store(admin);
        await session.SaveChangesAsync();

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(roles: ["SuperAdmin"], userId: admin.Id.ToString()));

        var id = await EntryAsync(client, type);
        var res = await client.PutAsJsonAsync($"/api/contents/{id}/status", new { id, transition = "Submit" });

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "a separation of duties an administrator can ignore is not one");
    }
}
