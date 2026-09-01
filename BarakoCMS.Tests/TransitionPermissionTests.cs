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

    /// <summary>
    /// The same manager, with the rule stored as "approve" against a transition declared "Approve".
    /// </summary>
    private static ContentTypePermission ManagerInAnotherCasing(string type) => new()
    {
        ContentTypeSlug = type,
        Read = new PermissionRule { Enabled = true },
        Transitions = new(StringComparer.OrdinalIgnoreCase)
        {
            ["approve"] = new PermissionRule { Enabled = true },
        },
    };

    /// <summary>A role with the content type named and nothing on it enabled.</summary>
    private static ContentTypePermission Nothing(string type) => new() { ContentTypeSlug = type };

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

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "the manager role grants a transition and not Update");
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

    /// <summary>
    /// A rule saved in one casing matches a transition declared in another.
    /// </summary>
    /// <remarks>
    /// Transitions is constructed with StringComparer.OrdinalIgnoreCase and that comparer does not
    /// survive persistence: System.Text.Json builds a fresh Dictionary with the default comparer
    /// when Marten deserialises the role, so the lookup silently became case sensitive as soon as
    /// the document was reloaded. The lifecycle matches a transition name case insensitively, so
    /// the two halves disagreed and the result was a 403 on a permission the admin UI showed as
    /// granted.
    ///
    /// This has to go through the database to mean anything. Asserting against a Role still held in
    /// memory tests the comparer in the initialiser, which was never the broken part.
    /// </remarks>
    [Fact]
    public async Task A_rule_saved_in_another_casing_still_matches()
    {
        var type = await TypeAsync();
        var (clerk, _) = await UserAsync(type, Clerk(type));
        var (submitter, _) = await UserAsync(type, Clerk(type));
        var (manager, _) = await UserAsync(type, ManagerInAnotherCasing(type));

        var id = await EntryAsync(clerk, type);
        await submitter.PutAsJsonAsync($"/api/contents/{id}/status", new { id, transition = "Submit" });

        var res = await manager.PutAsJsonAsync($"/api/contents/{id}/status", new { id, transition = "Approve" });

        res.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", res.StatusCode, await res.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// Someone with no rights on the type is not told what its transitions are.
    /// </summary>
    /// <remarks>
    /// Dropping the shared Update check is what opened this. The refusals in the transition path
    /// name the type's declared transitions and the entry's lifecycle state, and with no check above
    /// them any authenticated token could read a workflow map off a 400 and a 409.
    ///
    /// Read is the floor rather than Update. Requiring Update here would put back the coupling the
    /// whole change exists to remove, and a manager who may approve without editing has to get past
    /// this line.
    /// </remarks>
    [Fact]
    public async Task Someone_with_no_rights_on_the_type_is_not_told_its_transitions()
    {
        var type = await TypeAsync();
        var (clerk, _) = await UserAsync(type, Clerk(type));
        var (outsider, _) = await UserAsync(type, Nothing(type));

        var id = await EntryAsync(clerk, type);

        var res = await outsider.PutAsJsonAsync($"/api/contents/{id}/status", new { id, transition = "NoSuchThing" });
        var body = await res.Content.ReadAsStringAsync();

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden, "got {0}: {1}", res.StatusCode, body);
        body.Should().NotContain("Approve", "the declared transitions are a workflow map, not a 400 message");
        body.Should().NotContain("Submit");
    }

    /// <summary>
    /// A transition permission does not stand in for read.
    /// </summary>
    /// <remarks>
    /// This role grants Approve and leaves Read off, which is a configuration an operator can reach
    /// by granting the transition and forgetting the rest. Without the read floor it walks straight
    /// past the transition check and is told the entry's lifecycle state by the 409, so the floor is
    /// the only thing between this caller and the answer.
    ///
    /// It is also the rule worth stating on its own: acting on an entry you may not read is not a
    /// permission anybody meant to grant.
    /// </remarks>
    [Fact]
    public async Task A_transition_permission_does_not_stand_in_for_read()
    {
        var type = await TypeAsync();
        var (clerk, _) = await UserAsync(type, Clerk(type));
        var (ghost, _) = await UserAsync(type, new ContentTypePermission
        {
            ContentTypeSlug = type,
            Transitions = new(StringComparer.OrdinalIgnoreCase)
            {
                ["Approve"] = new PermissionRule { Enabled = true },
            },
        });

        // Left in Draft, so the state check would answer 409 and name it.
        var id = await EntryAsync(clerk, type);

        var res = await ghost.PutAsJsonAsync($"/api/contents/{id}/status", new { id, transition = "Approve" });
        var body = await res.Content.ReadAsStringAsync();

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden, "got {0}: {1}", res.StatusCode, body);
        body.Should().NotContain("Draft", "the entry's position in the lifecycle is not public either");
    }

    /// <summary>
    /// A refusal says the caller may not do it, rather than to come back later.
    /// </summary>
    /// <remarks>
    /// The clerk role grants Submit and not Approve, and the entry is in Draft, so both the
    /// permission check and the state check would refuse. Which one answers decides what the caller
    /// is told: 409 reads as "not yet, try once it is Submitted", and the clerk can never approve it
    /// at any state. The permission check runs first so the answer is the true one.
    /// </remarks>
    [Fact]
    public async Task Someone_who_may_not_perform_a_transition_is_not_told_to_come_back_later()
    {
        var type = await TypeAsync();
        var (clerk, _) = await UserAsync(type, Clerk(type));
        var (other, _) = await UserAsync(type, Clerk(type));

        var id = await EntryAsync(clerk, type);

        var res = await other.PutAsJsonAsync($"/api/contents/{id}/status", new { id, transition = "Approve" });

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "the clerk cannot approve at any state, so 409 would be the wrong answer");
    }

    /// <summary>
    /// The control for the two above: an out-of-order transition still explains itself to someone
    /// entitled to perform it.
    /// </summary>
    /// <remarks>
    /// Without this, refusing every transition with a bare 403 would satisfy both of the disclosure
    /// tests while removing the message an operator needs.
    /// </remarks>
    [Fact]
    public async Task An_out_of_order_transition_still_names_the_state_for_someone_who_may_perform_it()
    {
        var type = await TypeAsync();
        var (clerk, _) = await UserAsync(type, Clerk(type));
        var (manager, _) = await UserAsync(type, Manager(type));

        // Left in Draft, so Approve (Submitted to Approved) does not apply yet.
        var id = await EntryAsync(clerk, type);

        var res = await manager.PutAsJsonAsync($"/api/contents/{id}/status", new { id, transition = "Approve" });
        var body = await res.Content.ReadAsStringAsync();

        res.StatusCode.Should().Be(HttpStatusCode.Conflict, "got {0}: {1}", res.StatusCode, body);
        body.Should().Contain("Draft", "an operator entitled to approve is told why it did not apply");
    }
}
