using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.Tenants;

/// <summary>
/// A tenant can have a second member.
/// </summary>
/// <remarks>
/// One thing created a <c>Membership</c>: <c>POST /api/tenants</c>, provisioning the creator as an
/// Active admin. The model supported a roster, per-tenant roles and removal, and nothing exposed
/// any of it, so whoever set a tenant up did everything and the people it was set up for never
/// signed in. The limit was neither a unique index nor a validation rule: it was that no endpoint
/// wrote a second row.
///
/// Every request carries its own client IP so a 429 from the shared rate-limit bucket can never
/// stand in for a refusal, and the tenant claim is minted the way <c>TokenIssuer</c> mints it, so
/// these go through <c>TenantAccessMiddleware</c> as deployed.
/// </remarks>
[Collection("Integration1")]
public class TenantMemberApiTests
{
    private readonly IntegrationTestFixture _factory;
    private static int _ipCounter;

    public TenantMemberApiTests(IntegrationTestFixture factory) => _factory = factory;

    private static string NextIp() =>
        $"198.51.100.{Interlocked.Increment(ref _ipCounter) % 250 + 1}"; // TEST-NET-2

    private async Task<string> TenantAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var slug = $"members-{Guid.NewGuid():N}"[..16].ToLowerInvariant();
        session.Store(new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = slug });
        await session.SaveChangesAsync();
        return slug;
    }

    private async Task<Guid> UserAsync(string email, string? password = null)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var id = Guid.NewGuid();
        session.Store(new User
        {
            Id = id,
            Username = $"mem-{Guid.NewGuid():n}"[..14],
            Email = email,
            PasswordHash = password is null ? string.Empty : BCrypt.Net.BCrypt.HashPassword(password),
        });
        await session.SaveChangesAsync();
        return id;
    }

    private async Task MembershipAsync(Guid userId, string slug, MembershipStatus status, params Guid[] roleIds)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new Membership
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TenantSlug = slug,
            Status = status,
            RoleIds = roleIds.ToList(),
        });
        await session.SaveChangesAsync();
    }

    /// <summary>An administrator of <paramref name="slug"/>, holding the roles named.</summary>
    private async Task<HttpClient> AdminOfAsync(string slug, params string[] roles)
    {
        var actual = roles.Length == 0 ? new[] { "Admin" } : roles;
        var userId = await UserAsync($"admin-{Guid.NewGuid():n}@example.com");
        await MembershipAsync(userId, slug, MembershipStatus.Active, SystemRoles.AdminRoleId);

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(
                roles: actual,
                userId: userId.ToString(),
                additionalClaims: new Dictionary<string, string> { ["tenant"] = slug }));
        client.DefaultRequestHeaders.Add("X-Tenant", slug);
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, NextIp());
        return client;
    }

    private async Task<IReadOnlyList<Membership>> MembershipsAsync(Guid userId, string slug)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return await session.Query<Membership>()
            .Where(m => m.UserId == userId && m.TenantSlug == slug)
            .ToListAsync();
    }

    private async Task<User?> UserByEmailAsync(string email)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return await session.Query<User>().FirstOrDefaultAsync(u => u.Email == email);
    }

    private static async Task<List<Guid>> RosterUserIdsAsync(HttpResponseMessage response)
    {
        response.IsSuccessStatusCode.Should().BeTrue("the roster returned {0}", response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("items")
            .EnumerateArray()
            .Select(item => item.GetProperty("userId").GetGuid())
            .ToList();
    }

    private static void NotRateLimited(HttpResponseMessage response) =>
        response.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests,
            "a rate-limited response proves nothing about this endpoint's behaviour");

    // The headline. Nothing but tenant creation ever wrote a Membership, so a tenant had exactly
    // one member for its whole life.
    [Fact]
    public async Task A_tenant_can_be_given_a_second_member()
    {
        var slug = await TenantAsync();
        var client = await AdminOfAsync(slug);
        var email = $"second-{Guid.NewGuid():n}@example.com";

        var added = await client.PostAsJsonAsync("/api/tenants/members",
            new { email, roleIds = new[] { SystemRoles.UserRoleId } });

        NotRateLimited(added);
        added.StatusCode.Should().Be(HttpStatusCode.OK);

        var roster = await RosterUserIdsAsync(await client.GetAsync("/api/tenants/members"));
        roster.Should().HaveCount(2, "the administrator who set the tenant up, plus the person they added");
    }

    [Fact]
    public async Task The_roster_returns_only_the_callers_tenant()
    {
        var mine = await TenantAsync();
        var theirs = await TenantAsync();

        var client = await AdminOfAsync(mine);
        var outsider = await UserAsync($"outsider-{Guid.NewGuid():n}@example.com");
        await MembershipAsync(outsider, theirs, MembershipStatus.Active, SystemRoles.AdminRoleId);

        var roster = await RosterUserIdsAsync(await client.GetAsync("/api/tenants/members"));

        roster.Should().NotContain(outsider,
            "Membership is SingleTenanted, so the slug filter on the query is the whole isolation "
          + "guarantee here and nothing underneath it applies one");
    }

    [Fact]
    public async Task A_second_tenants_members_are_invisible_even_when_both_rosters_are_populated()
    {
        // The positive control for the test above: an empty other tenant would pass a query that
        // filtered on nothing at all.
        var mine = await TenantAsync();
        var theirs = await TenantAsync();

        var client = await AdminOfAsync(mine);
        await client.PostAsJsonAsync("/api/tenants/members",
            new { email = $"ours-{Guid.NewGuid():n}@example.com", roleIds = Array.Empty<Guid>() });

        var otherClient = await AdminOfAsync(theirs);
        await otherClient.PostAsJsonAsync("/api/tenants/members",
            new { email = $"theirs-{Guid.NewGuid():n}@example.com", roleIds = Array.Empty<Guid>() });

        var mineRoster = await RosterUserIdsAsync(await client.GetAsync("/api/tenants/members"));
        var theirsRoster = await RosterUserIdsAsync(await otherClient.GetAsync("/api/tenants/members"));

        mineRoster.Should().HaveCount(2);
        theirsRoster.Should().HaveCount(2);
        mineRoster.Should().NotIntersectWith(theirsRoster);
    }

    [Fact]
    public async Task SuperAdmin_is_refused_even_when_the_caller_holds_it()
    {
        // The one escalation this surface could offer: an administrator of any tenant granting
        // themselves, or anyone else, platform-wide access through a per-tenant route.
        var slug = await TenantAsync();
        var client = await AdminOfAsync(slug, "SuperAdmin", "Admin");

        var response = await client.PostAsJsonAsync("/api/tenants/members",
            new { email = $"escalate-{Guid.NewGuid():n}@example.com", roleIds = new[] { SystemRoles.SuperAdminRoleId } });

        NotRateLimited(response);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SuperAdmin_is_not_offered_as_an_assignable_role()
    {
        var slug = await TenantAsync();
        var client = await AdminOfAsync(slug, "SuperAdmin", "Admin");

        var response = await client.GetAsync("/api/tenants/members/roles");
        response.IsSuccessStatusCode.Should().BeTrue("assignable roles returned {0}", response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var ids = document.RootElement.GetProperty("items")
            .EnumerateArray().Select(r => r.GetProperty("id").GetGuid()).ToList();

        ids.Should().NotBeEmpty("Admin, HR and User are all assignable inside a tenant");
        ids.Should().NotContain(SystemRoles.SuperAdminRoleId,
            "the list a client is offered and the list the server accepts must not drift apart");
    }

    [Fact]
    public async Task The_assignable_roles_list_leaves_out_roles_the_caller_cannot_grant()
    {
        var slug = await TenantAsync();
        var plain = await StoreRoleAsync(slug, [], null);
        var type = $"Lst{Guid.NewGuid():N}"[..12];
        var content = await StoreRoleAsync(slug, [], type);
        var capability = await StoreRoleAsync(slug, [SystemCapabilities.ManageApiKeys], null);

        async Task<List<Guid>> IdsAsync(HttpClient client)
        {
            var response = await client.GetAsync("/api/tenants/members/roles");
            response.IsSuccessStatusCode.Should().BeTrue("assignable roles returned {0}", response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return document.RootElement.GetProperty("items")
                .EnumerateArray().Select(r => r.GetProperty("id").GetGuid()).ToList();
        }

        var (weak, _) = await ManagerAsync(slug);
        var offered = await IdsAsync(weak);
        offered.Should().Contain(plain.Id);
        offered.Should().NotContain(content.Id, "the caller does not hold read on the role's type");
        offered.Should().NotContain(capability.Id, "the caller does not hold manage_api_keys");

        var super = await IdsAsync(await AdminOfAsync(slug, "SuperAdmin", "Admin"));
        super.Should().Contain([plain.Id, content.Id, capability.Id]);
    }

    [Fact]
    public async Task An_unknown_email_creates_an_otp_only_user()
    {
        // The branch a happy-path test misses. An invited person has no password: they sign in with
        // an emailed code.
        var slug = await TenantAsync();
        var client = await AdminOfAsync(slug);
        var email = $"invited-{Guid.NewGuid():n}@example.com";

        (await UserByEmailAsync(email)).Should().BeNull("the arrangement depends on this email being new");

        var response = await client.PostAsJsonAsync("/api/tenants/members",
            new { email, roleIds = new[] { SystemRoles.UserRoleId } });

        NotRateLimited(response);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var created = await UserByEmailAsync(email);
        created.Should().NotBeNull();
        created!.PasswordHash.Should().BeEmpty("an invited member signs in with an emailed code, not a password");
    }

    [Fact]
    public async Task A_known_email_reuses_the_existing_user()
    {
        // One person, one account, however many tenants they belong to.
        var slug = await TenantAsync();
        var client = await AdminOfAsync(slug);
        var email = $"existing-{Guid.NewGuid():n}@example.com";
        var existing = await UserAsync(email, password: "P@ssword123!");

        var response = await client.PostAsJsonAsync("/api/tenants/members",
            new { email, roleIds = Array.Empty<Guid>() });

        NotRateLimited(response);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("userId").GetGuid().Should().Be(existing);

        var stillHasAPassword = await UserByEmailAsync(email);
        stillHasAPassword!.PasswordHash.Should().NotBeEmpty("adding somebody to a tenant is not a reset");
    }

    [Fact]
    public async Task Removing_a_member_marks_them_rather_than_deleting_the_row()
    {
        var slug = await TenantAsync();
        var client = await AdminOfAsync(slug);
        var email = $"leaver-{Guid.NewGuid():n}@example.com";

        var added = await client.PostAsJsonAsync("/api/tenants/members",
            new { email, roleIds = Array.Empty<Guid>() });
        added.StatusCode.Should().Be(HttpStatusCode.OK);
        var userId = JsonDocument.Parse(await added.Content.ReadAsStringAsync())
            .RootElement.GetProperty("userId").GetGuid();

        var removed = await client.DeleteAsync($"/api/tenants/members/{userId}");
        NotRateLimited(removed);
        removed.StatusCode.Should().Be(HttpStatusCode.OK);

        var rows = await MembershipsAsync(userId, slug);
        rows.Should().ContainSingle("the row survives so history and audit survive with it")
            .Which.Status.Should().Be(MembershipStatus.Removed);

        var roster = await RosterUserIdsAsync(await client.GetAsync("/api/tenants/members"));
        roster.Should().NotContain(userId, "a removed member is off the roster");
    }

    [Fact]
    public async Task Re_adding_a_removed_member_reactivates_the_row_they_already_had()
    {
        var slug = await TenantAsync();
        var client = await AdminOfAsync(slug);
        var email = $"returner-{Guid.NewGuid():n}@example.com";

        var added = await client.PostAsJsonAsync("/api/tenants/members",
            new { email, roleIds = Array.Empty<Guid>() });
        var userId = JsonDocument.Parse(await added.Content.ReadAsStringAsync())
            .RootElement.GetProperty("userId").GetGuid();

        (await client.DeleteAsync($"/api/tenants/members/{userId}")).StatusCode
            .Should().Be(HttpStatusCode.OK);

        var again = await client.PostAsJsonAsync("/api/tenants/members",
            new { email, roleIds = new[] { SystemRoles.UserRoleId } });
        NotRateLimited(again);
        again.StatusCode.Should().Be(HttpStatusCode.OK);

        var rows = await MembershipsAsync(userId, slug);
        rows.Should().ContainSingle("a second row for the same pair would make role resolution "
                                  + "depend on which one it happened to read first")
            .Which.Status.Should().Be(MembershipStatus.Active);
    }

    [Fact]
    public async Task A_members_roles_and_status_can_be_changed_within_the_tenant()
    {
        var slug = await TenantAsync();
        var client = await AdminOfAsync(slug);
        // A tenant-owned role: the seeded HR role is global and carries view_sensitive, which a tenant Admin cannot hand out.
        var role = await StoreRoleAsync(slug, [], null);
        var email = $"promoted-{Guid.NewGuid():n}@example.com";

        var added = await client.PostAsJsonAsync("/api/tenants/members",
            new { email, roleIds = new[] { SystemRoles.UserRoleId } });
        var userId = JsonDocument.Parse(await added.Content.ReadAsStringAsync())
            .RootElement.GetProperty("userId").GetGuid();

        var updated = await client.PutAsJsonAsync($"/api/tenants/members/{userId}",
            new { roleIds = new[] { role.Id }, status = "Suspended" });

        NotRateLimited(updated);
        updated.StatusCode.Should().Be(HttpStatusCode.OK);

        var row = (await MembershipsAsync(userId, slug)).Single();
        row.Status.Should().Be(MembershipStatus.Suspended);
        row.RoleIds.Should().Equal(role.Id);
    }

    [Fact]
    public async Task An_update_cannot_grant_SuperAdmin_either()
    {
        // Refusing it on the add path and allowing it on the edit path would be the same hole with
        // one more request in front of it.
        var slug = await TenantAsync();
        var client = await AdminOfAsync(slug, "SuperAdmin", "Admin");
        var email = $"editescalate-{Guid.NewGuid():n}@example.com";

        var added = await client.PostAsJsonAsync("/api/tenants/members",
            new { email, roleIds = Array.Empty<Guid>() });
        var userId = JsonDocument.Parse(await added.Content.ReadAsStringAsync())
            .RootElement.GetProperty("userId").GetGuid();

        var response = await client.PutAsJsonAsync($"/api/tenants/members/{userId}",
            new { roleIds = new[] { SystemRoles.SuperAdminRoleId }, status = "Active" });

        NotRateLimited(response);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_member_of_another_tenant_cannot_be_edited_through_this_tenant()
    {
        var mine = await TenantAsync();
        var theirs = await TenantAsync();

        var client = await AdminOfAsync(mine);
        var outsider = await UserAsync($"theirmember-{Guid.NewGuid():n}@example.com");
        await MembershipAsync(outsider, theirs, MembershipStatus.Active, SystemRoles.AdminRoleId);

        var response = await client.DeleteAsync($"/api/tenants/members/{outsider}");

        NotRateLimited(response);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var theirRow = (await MembershipsAsync(outsider, theirs)).Single();
        theirRow.Status.Should().Be(MembershipStatus.Active, "their membership is untouched");

        // The control. A 404 from a route that does not exist would pass everything above, so the
        // same client removing somebody in its own tenant has to succeed in the same test.
        var ours = await client.PostAsJsonAsync("/api/tenants/members",
            new { email = $"ourmember-{Guid.NewGuid():n}@example.com", roleIds = Array.Empty<Guid>() });
        ours.StatusCode.Should().Be(HttpStatusCode.OK);
        var ourUserId = JsonDocument.Parse(await ours.Content.ReadAsStringAsync())
            .RootElement.GetProperty("userId").GetGuid();

        (await client.DeleteAsync($"/api/tenants/members/{ourUserId}")).StatusCode
            .Should().Be(HttpStatusCode.OK, "the route exists and the refusal above was about the tenant");
    }

    // A members manager with one custom role (which carries nothing but the manager capability),
    // and any roles they also hold.
    private async Task<(HttpClient Client, Guid UserId)> ManagerAsync(string slug, params Guid[] alsoHolds)
    {
        var role = await StoreRoleAsync(slug, [SystemCapabilities.ManageTenantMembers], null);
        var userId = await UserAsync($"mgr-{Guid.NewGuid():n}@example.com");
        await MembershipAsync(userId, slug, MembershipStatus.Active, [role.Id, .. alsoHolds]);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(
                roles: [role.Name], userId: userId.ToString(),
                additionalClaims: new Dictionary<string, string> { ["tenant"] = slug }));
        client.DefaultRequestHeaders.Add("X-Tenant", slug);
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, NextIp());
        return (client, userId);
    }

    private async Task<Role> StoreRoleAsync(string slug, string[] capabilities, string? readType)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var role = new Role
        {
            Id = Guid.NewGuid(), Name = $"mem-role-{Guid.NewGuid():N}"[..20], TenantSlug = slug,
            SystemCapabilities = capabilities.ToList(),
            Permissions = readType is null ? new() : [new ContentTypePermission
            {
                ContentTypeSlug = readType, Read = new PermissionRule { Enabled = true },
            }],
        };
        session.Store(role);
        await session.SaveChangesAsync();
        return role;
    }

    [Fact]
    public async Task A_weaker_caller_cannot_add_or_update_a_member_with_more_than_they_hold()
    {
        var slug = await TenantAsync();
        var type = $"Mem{Guid.NewGuid():N}"[..12];
        var content = await StoreRoleAsync(slug, [], type);
        var capability = await StoreRoleAsync(slug, [SystemCapabilities.ManageApiKeys], null);
        var (weak, _) = await ManagerAsync(slug);

        var email = $"weak-{Guid.NewGuid():n}@example.com";
        var addContent = await weak.PostAsJsonAsync("/api/tenants/members", new { email, roleIds = new[] { content.Id } });
        addContent.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await addContent.Content.ReadAsStringAsync()).Should().Contain($"'read' on '{type}' because you do not hold it");

        var addCapability = await weak.PostAsJsonAsync("/api/tenants/members", new { email, roleIds = new[] { capability.Id } });
        addCapability.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var target = await UserAsync($"target-{Guid.NewGuid():n}@example.com");
        await MembershipAsync(target, slug, MembershipStatus.Active, SystemRoles.UserRoleId);
        var update = await weak.PutAsJsonAsync($"/api/tenants/members/{target}",
            new { roleIds = new[] { content.Id }, status = "Active" });
        update.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        (await MembershipsAsync(target, slug)).Single().RoleIds.Should().Equal(SystemRoles.UserRoleId);
    }

    [Fact]
    public async Task A_weaker_caller_can_drop_a_role_but_not_add_one_they_do_not_hold()
    {
        var slug = await TenantAsync();
        var type = $"Mem{Guid.NewGuid():N}"[..12];
        var content = await StoreRoleAsync(slug, [], type);
        var other = await StoreRoleAsync(slug, [], $"Oth{Guid.NewGuid():N}"[..12]);
        var (weak, _) = await ManagerAsync(slug);
        var target = await UserAsync($"strong-{Guid.NewGuid():n}@example.com");
        await MembershipAsync(target, slug, MembershipStatus.Active, content.Id);

        var add = await weak.PutAsJsonAsync($"/api/tenants/members/{target}",
            new { roleIds = new[] { content.Id, other.Id }, status = "Active" });
        add.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await MembershipsAsync(target, slug)).Single().RoleIds.Should().Equal(content.Id);

        var drop = await weak.PutAsJsonAsync($"/api/tenants/members/{target}",
            new { roleIds = Array.Empty<Guid>(), status = "Active" });
        drop.StatusCode.Should().Be(HttpStatusCode.OK, await drop.Content.ReadAsStringAsync());
        (await MembershipsAsync(target, slug)).Single().RoleIds.Should().BeEmpty();
    }

    [Fact]
    public async Task Only_a_caller_who_holds_the_roles_can_reactivate_a_suspended_member()
    {
        var slug = await TenantAsync();
        var type = $"Mem{Guid.NewGuid():N}"[..12];
        var content = await StoreRoleAsync(slug, [], type);
        var (weak, _) = await ManagerAsync(slug);
        var (strong, _) = await ManagerAsync(slug, content.Id);
        var target = await UserAsync($"suspended-{Guid.NewGuid():n}@example.com");
        await MembershipAsync(target, slug, MembershipStatus.Suspended, content.Id);

        var denied = await weak.PutAsJsonAsync($"/api/tenants/members/{target}",
            new { roleIds = new[] { content.Id }, status = "Active" });
        denied.StatusCode.Should().Be(HttpStatusCode.Forbidden, await denied.Content.ReadAsStringAsync());
        (await MembershipsAsync(target, slug)).Single().Status.Should().Be(MembershipStatus.Suspended);

        var allowed = await strong.PutAsJsonAsync($"/api/tenants/members/{target}",
            new { roleIds = new[] { content.Id }, status = "Active" });
        allowed.StatusCode.Should().Be(HttpStatusCode.OK, await allowed.Content.ReadAsStringAsync());
        (await MembershipsAsync(target, slug)).Single().Status.Should().Be(MembershipStatus.Active);
    }

    [Fact]
    public async Task A_caller_can_add_or_update_a_member_with_roles_they_hold()
    {
        var slug = await TenantAsync();
        var type = $"Mem{Guid.NewGuid():N}"[..12];
        var content = await StoreRoleAsync(slug, [], type);
        var (manager, _) = await ManagerAsync(slug, content.Id);

        var email = $"held-{Guid.NewGuid():n}@example.com";
        var added = await manager.PostAsJsonAsync("/api/tenants/members", new { email, roleIds = new[] { content.Id } });
        added.StatusCode.Should().Be(HttpStatusCode.OK, await added.Content.ReadAsStringAsync());
        var userId = JsonDocument.Parse(await added.Content.ReadAsStringAsync()).RootElement.GetProperty("userId").GetGuid();

        var updated = await manager.PutAsJsonAsync($"/api/tenants/members/{userId}",
            new { roleIds = new[] { content.Id }, status = "Suspended" });
        updated.StatusCode.Should().Be(HttpStatusCode.OK, await updated.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_plain_tenant_Admin_can_add_a_member_with_a_content_role()
    {
        var slug = await TenantAsync();
        var type = $"Mem{Guid.NewGuid():N}"[..12];
        var content = await StoreRoleAsync(slug, [], type);
        var client = await AdminOfAsync(slug);

        var added = await client.PostAsJsonAsync("/api/tenants/members",
            new { email = $"admin-adds-{Guid.NewGuid():n}@example.com", roleIds = new[] { content.Id } });
        added.StatusCode.Should().Be(HttpStatusCode.OK, await added.Content.ReadAsStringAsync());
    }
}
