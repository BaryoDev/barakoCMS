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
/// Who may write the member profile a permission condition reads.
/// </summary>
/// <remarks>
/// A rule such as <c>Branch eq $CURRENT_USER.region</c> trusts the profile, so writing a profile is
/// granting access. The only writers are the two member routes, behind the capability that already
/// lets its holder assign roles in the tenant. A member changing their own attribute, or an
/// administrator of one tenant changing a profile in another, would each be a way to grant what
/// the caller could not grant before.
///
/// Requests carry their own client IP and a tenant claim for the reasons
/// <see cref="TenantMemberApiTests"/> gives.
/// </remarks>
[Collection("Integration1")]
public class MemberProfileApiTests
{
    private readonly IntegrationTestFixture _factory;
    private static int _ipCounter;

    public MemberProfileApiTests(IntegrationTestFixture factory) => _factory = factory;

    private static string NextIp() =>
        $"198.51.100.{Interlocked.Increment(ref _ipCounter) % 250 + 1}";

    private async Task<string> TenantAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var slug = $"profile-{Guid.NewGuid():N}"[..16].ToLowerInvariant();
        session.Store(new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = slug });
        await session.SaveChangesAsync();
        return slug;
    }

    private static string NewEmail() => $"prof-{Guid.NewGuid():n}@example.com";

    private async Task<Guid> UserAsync(string? email = null)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var id = Guid.NewGuid();
        session.Store(new User
        {
            Id = id,
            Username = $"prof-{Guid.NewGuid():n}"[..14],
            Email = email ?? NewEmail(),
            PasswordHash = string.Empty,
        });
        await session.SaveChangesAsync();
        return id;
    }

    private async Task MembershipAsync(
        Guid userId, string slug, Guid roleId, Dictionary<string, string>? profile = null)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new Membership
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TenantSlug = slug,
            Status = MembershipStatus.Active,
            RoleIds = [roleId],
            Profile = profile ?? new(),
        });
        await session.SaveChangesAsync();
    }

    private HttpClient ClientFor(Guid userId, string slug, string roleName)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(
                roles: [roleName],
                userId: userId.ToString(),
                additionalClaims: new Dictionary<string, string> { ["tenant"] = slug }));
        client.DefaultRequestHeaders.Add("X-Tenant", slug);
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, NextIp());
        return client;
    }

    private async Task<HttpClient> AdminOfAsync(string slug)
    {
        var userId = await UserAsync();
        await MembershipAsync(userId, slug, SystemRoles.AdminRoleId);
        return ClientFor(userId, slug, "Admin");
    }

    /// <summary>A member of the tenant holding the User role, with the profile given.</summary>
    private async Task<Guid> MemberAsync(
        string slug, Dictionary<string, string>? profile = null, string? email = null)
    {
        var userId = await UserAsync(email);
        await MembershipAsync(userId, slug, SystemRoles.UserRoleId, profile);
        return userId;
    }

    /// <summary>The newest audit entry of an action about a member, as JSON.</summary>
    private async Task<JsonElement> LastAuditAsync(string slug, string action, Guid target)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var targetId = target.ToString();

        var entries = await session.Query<AuditEvent>()
            .Where(e => e.TenantSlug == slug && e.Action == action && e.TargetId == targetId)
            .ToListAsync();

        entries.Should().NotBeEmpty("the write was audited");
        var metadata = entries.OrderByDescending(e => e.CreatedAt).First().Metadata;
        metadata.Should().NotBeNull();

        return JsonDocument.Parse(JsonSerializer.Serialize(metadata)).RootElement.Clone();
    }

    private static List<string> Names(JsonElement metadata, string key) =>
        metadata.GetProperty(key).EnumerateArray().Select(name => name.GetString()!).ToList();

    private async Task<Membership> RowAsync(Guid userId, string slug)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var rows = await session.Query<Membership>()
            .Where(m => m.UserId == userId && m.TenantSlug == slug)
            .ToListAsync();

        return rows.Should().ContainSingle("one membership per user and tenant").Which;
    }

    private static Dictionary<string, string> Branch(string value) => new() { ["region"] = value };

    private static object Edit(Dictionary<string, string>? profile) => profile is null
        ? new { roleIds = new[] { SystemRoles.UserRoleId }, status = "Active" }
        : new { roleIds = new[] { SystemRoles.UserRoleId }, status = "Active", profile };

    private static void NotRateLimited(HttpResponseMessage response) =>
        response.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests,
            "a rate-limited response proves nothing about this endpoint's behaviour");

    [Fact]
    public async Task An_administrator_sets_a_members_profile()
    {
        var slug = await TenantAsync();
        var admin = await AdminOfAsync(slug);
        var member = await MemberAsync(slug);

        var response = await admin.PutAsJsonAsync($"/api/tenants/members/{member}",
            Edit(new Dictionary<string, string> { ["region"] = "north", ["ward"] = "7" }));

        NotRateLimited(response);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        var stored = (await RowAsync(member, slug)).Profile;
        stored.Should().HaveCount(2);
        stored.Should().Contain("region", "north").And.Contain("ward", "7");

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("profile").GetProperty("region").GetString().Should().Be("north");
    }

    [Fact]
    public async Task A_profile_can_be_given_when_the_member_is_added()
    {
        var slug = await TenantAsync();
        var admin = await AdminOfAsync(slug);

        var added = await admin.PostAsJsonAsync("/api/tenants/members", new
        {
            email = $"added-{Guid.NewGuid():n}@example.com",
            roleIds = new[] { SystemRoles.UserRoleId },
            profile = Branch("south"),
        });

        NotRateLimited(added);
        added.StatusCode.Should().Be(HttpStatusCode.OK, await added.Content.ReadAsStringAsync());

        using var body = JsonDocument.Parse(await added.Content.ReadAsStringAsync());
        var userId = body.RootElement.GetProperty("userId").GetGuid();

        var stored = (await RowAsync(userId, slug)).Profile;
        stored.Should().HaveCount(1);
        stored.Should().Contain("region", "south");
    }

    [Fact]
    public async Task An_update_that_sends_no_profile_keeps_the_stored_one()
    {
        var slug = await TenantAsync();
        var admin = await AdminOfAsync(slug);
        var member = await MemberAsync(slug, Branch("north"));

        var response = await admin.PutAsJsonAsync($"/api/tenants/members/{member}", Edit(null));

        NotRateLimited(response);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var stored = (await RowAsync(member, slug)).Profile;
        stored.Should().HaveCount(1, "a client that knows nothing of profiles edited roles and nothing else");
        stored.Should().Contain("region", "north");
    }

    [Fact]
    public async Task An_update_that_sends_an_empty_profile_clears_it()
    {
        var slug = await TenantAsync();
        var admin = await AdminOfAsync(slug);
        var member = await MemberAsync(slug, Branch("north"));

        var response = await admin.PutAsJsonAsync($"/api/tenants/members/{member}", Edit(new Dictionary<string, string>()));

        NotRateLimited(response);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await RowAsync(member, slug)).Profile.Should().BeEmpty();
    }

    [Fact]
    public async Task A_member_cannot_change_their_own_profile()
    {
        var slug = await TenantAsync();
        var member = await MemberAsync(slug, Branch("south"));
        var asMember = ClientFor(member, slug, "User");

        var response = await asMember.PutAsJsonAsync($"/api/tenants/members/{member}", Edit(Branch("north")));

        NotRateLimited(response);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var stored = (await RowAsync(member, slug)).Profile;
        stored.Should().HaveCount(1);
        stored.Should().Contain("region", "south", "the attribute a rule trusts is not the member's to set");

        // The control: the route exists and the refusal was about who asked.
        var admin = await AdminOfAsync(slug);
        var allowed = await admin.PutAsJsonAsync($"/api/tenants/members/{member}", Edit(Branch("north")));
        allowed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await RowAsync(member, slug)).Profile.Should().Contain("region", "north");
    }

    [Fact]
    public async Task A_member_cannot_add_themselves_again_with_a_profile()
    {
        // POST reactivates and rewrites the row of somebody who is already a member, so it is a
        // second way to reach the same profile.
        var slug = await TenantAsync();
        var email = NewEmail();
        var userId = await MemberAsync(slug, Branch("south"), email);

        var response = await ClientFor(userId, slug, "User").PostAsJsonAsync("/api/tenants/members",
            new { email, roleIds = new[] { SystemRoles.UserRoleId }, profile = Branch("north") });

        NotRateLimited(response);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await RowAsync(userId, slug)).Profile.Should().Contain("region", "south");
    }

    [Fact]
    public async Task An_administrator_of_one_tenant_cannot_change_a_profile_in_another()
    {
        var mine = await TenantAsync();
        var theirs = await TenantAsync();
        var admin = await AdminOfAsync(mine);
        var outsider = await MemberAsync(theirs, Branch("south"));

        var response = await admin.PutAsJsonAsync($"/api/tenants/members/{outsider}", Edit(Branch("north")));

        NotRateLimited(response);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var stored = (await RowAsync(outsider, theirs)).Profile;
        stored.Should().HaveCount(1);
        stored.Should().Contain("region", "south", "their membership is untouched");

        // The control: the same client and body, against a member of its own tenant.
        var ours = await MemberAsync(mine, Branch("south"));
        var allowed = await admin.PutAsJsonAsync($"/api/tenants/members/{ours}", Edit(Branch("north")));
        allowed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await RowAsync(ours, mine)).Profile.Should().Contain("region", "north");
    }

    [Fact]
    public async Task A_person_in_two_tenants_has_a_profile_in_each()
    {
        var mine = await TenantAsync();
        var theirs = await TenantAsync();
        var admin = await AdminOfAsync(mine);

        var person = await UserAsync();
        await MembershipAsync(person, mine, SystemRoles.UserRoleId, Branch("south"));
        await MembershipAsync(person, theirs, SystemRoles.UserRoleId, Branch("south"));

        var response = await admin.PutAsJsonAsync($"/api/tenants/members/{person}", Edit(Branch("north")));

        NotRateLimited(response);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        (await RowAsync(person, mine)).Profile.Should().Contain("region", "north");
        (await RowAsync(person, theirs)).Profile.Should().Contain("region", "south",
            "an administrator here says nothing about the person's place in another tenant");
    }

    [Fact]
    public async Task A_profile_a_condition_could_not_read_is_refused_and_nothing_is_stored()
    {
        var slug = await TenantAsync();
        var admin = await AdminOfAsync(slug);
        var member = await MemberAsync(slug, Branch("south"));

        var refused = new (string Why, Dictionary<string, string> Profile)[]
        {
            ("a name with a space", new Dictionary<string, string> { ["branch name"] = "north" }),
            ("a name with a dot", new Dictionary<string, string> { ["branch.name"] = "north" }),
            ("a name starting with a digit", new Dictionary<string, string> { ["1branch"] = "north" }),
            ("an empty value", new Dictionary<string, string> { ["region"] = "" }),
            ("a value of only spaces", new Dictionary<string, string> { ["region"] = "   " }),
            ("a value past the limit", new Dictionary<string, string> { ["region"] = new string('n', 257) }),
            ("two names differing only by case", new Dictionary<string, string> { ["region"] = "north", ["Region"] = "north" }),
            ("more attributes than the limit", Enumerable.Range(0, 33).ToDictionary(i => $"a{i}", _ => "x")),
        };

        foreach (var (why, profile) in refused)
        {
            var response = await admin.PutAsJsonAsync($"/api/tenants/members/{member}", Edit(profile));

            NotRateLimited(response);
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, "{0} is refused", why);
        }

        var stored = (await RowAsync(member, slug)).Profile;
        stored.Should().HaveCount(1);
        stored.Should().Contain("region", "south", "a refused request changes nothing");
    }

    [Fact]
    public async Task Re_adding_a_removed_member_does_not_bring_their_old_profile_back()
    {
        var slug = await TenantAsync();
        var admin = await AdminOfAsync(slug);
        var email = $"returner-{Guid.NewGuid():n}@example.com";

        var added = await admin.PostAsJsonAsync("/api/tenants/members",
            new { email, roleIds = new[] { SystemRoles.UserRoleId }, profile = Branch("north") });
        added.StatusCode.Should().Be(HttpStatusCode.OK);
        var userId = JsonDocument.Parse(await added.Content.ReadAsStringAsync())
            .RootElement.GetProperty("userId").GetGuid();
        (await RowAsync(userId, slug)).Profile.Should().Contain("region", "north");

        (await admin.DeleteAsync($"/api/tenants/members/{userId}")).StatusCode.Should().Be(HttpStatusCode.OK);

        var again = await admin.PostAsJsonAsync("/api/tenants/members",
            new { email, roleIds = new[] { SystemRoles.UserRoleId } });
        NotRateLimited(again);
        again.StatusCode.Should().Be(HttpStatusCode.OK);

        (await RowAsync(userId, slug)).Profile.Should().BeEmpty(
            "the branch they held before they were removed is not one they were given now");
    }

    [Fact]
    public async Task A_roles_only_write_from_a_stale_read_does_not_bring_back_a_cleared_profile()
    {
        var slug = await TenantAsync();
        var admin = await AdminOfAsync(slug);
        var member = await MemberAsync(slug, Branch("north"));

        // One administrator's roles edit reads the row while the member still has a branch.
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var read = (await session.Query<Membership>()
            .Where(m => m.UserId == member && m.TenantSlug == slug)
            .ToListAsync()).Should().ContainSingle().Which;
        read.Profile.Should().Contain("region", "north", "the read has to be the stale one");

        // Another clears the profile, and that commits first.
        var cleared = await admin.PutAsJsonAsync($"/api/tenants/members/{member}",
            Edit(new Dictionary<string, string>()));
        cleared.StatusCode.Should().Be(HttpStatusCode.OK);
        (await RowAsync(member, slug)).Profile.Should().BeEmpty();

        // The first one's write, which is what both member routes queue for a request with no profile.
        await global::barakoCMS.Features.Tenants.Members.Members.ChangeAsync(
            session, new System.Security.Claims.ClaimsPrincipal(), "tenant.member.updated", read,
            [SystemRoles.HRRoleId], MembershipStatus.Active, profile: null,
            new Dictionary<string, object>(), CancellationToken.None);
        await session.SaveChangesAsync();

        var row = await RowAsync(member, slug);
        row.RoleIds.Should().Equal(SystemRoles.HRRoleId);
        row.Profile.Should().BeEmpty("a write that carries no profile cannot put one back");
    }

    [Fact]
    public async Task Adding_somebody_who_is_already_a_member_keeps_their_profile_when_the_request_has_none()
    {
        var slug = await TenantAsync();
        var admin = await AdminOfAsync(slug);
        var email = NewEmail();
        var member = await MemberAsync(slug, Branch("north"), email);

        var again = await admin.PostAsJsonAsync("/api/tenants/members",
            new { email, roleIds = new[] { SystemRoles.UserRoleId } });
        NotRateLimited(again);
        again.StatusCode.Should().Be(HttpStatusCode.OK, await again.Content.ReadAsStringAsync());

        var row = await RowAsync(member, slug);
        row.RoleIds.Should().Equal(SystemRoles.UserRoleId);
        row.Profile.Should().HaveCount(1);
        row.Profile.Should().Contain("region", "north", "a client that knows nothing of profiles must not erase one");

        using (var body = JsonDocument.Parse(await again.Content.ReadAsStringAsync()))
            body.RootElement.GetProperty("profile").GetProperty("region").GetString().Should().Be("north");

        // And one that does send a profile replaces it.
        var replaced = await admin.PostAsJsonAsync("/api/tenants/members",
            new { email, roleIds = new[] { SystemRoles.UserRoleId }, profile = Branch("south") });
        replaced.StatusCode.Should().Be(HttpStatusCode.OK);
        (await RowAsync(member, slug)).Profile.Should().Contain("region", "south");
    }

    [Fact]
    public async Task The_audit_entry_names_what_changed_in_a_profile_and_never_a_value()
    {
        var slug = await TenantAsync();
        var admin = await AdminOfAsync(slug);
        var member = await MemberAsync(slug, new Dictionary<string, string>
        {
            ["region"] = "north-branch-value",
            ["ward"] = "ward-seven-value",
        });

        var changed = await admin.PutAsJsonAsync($"/api/tenants/members/{member}", Edit(new Dictionary<string, string>
        {
            ["region"] = "south-branch-value",
            ["level"] = "level-three-value",
        }));
        NotRateLimited(changed);
        changed.StatusCode.Should().Be(HttpStatusCode.OK);

        var entry = await LastAuditAsync(slug, "tenant.member.updated", member);
        Names(entry, "profileAdded").Should().Equal("level");
        Names(entry, "profileRemoved").Should().Equal("ward");
        Names(entry, "profileChanged").Should().Equal("region");

        var text = entry.GetRawText();
        foreach (var value in new[] { "north-branch-value", "south-branch-value", "ward-seven-value", "level-three-value" })
            text.Should().NotContain(value, "the audit entry holds names, never values");
    }

    [Fact]
    public async Task The_audit_entry_says_nothing_about_a_profile_that_did_not_change()
    {
        var slug = await TenantAsync();
        var admin = await AdminOfAsync(slug);
        var member = await MemberAsync(slug, Branch("north"));

        // A roles-only edit, then one that sends the profile the member already has.
        foreach (var body in new[] { Edit(null), Edit(Branch("north")) })
        {
            var response = await admin.PutAsJsonAsync($"/api/tenants/members/{member}", body);
            NotRateLimited(response);
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var entry = await LastAuditAsync(slug, "tenant.member.updated", member);
            entry.TryGetProperty("roleIds", out _).Should().BeTrue("this is the entry for the edit");
            entry.EnumerateObject().Select(p => p.Name).Where(name => name.StartsWith("profile", StringComparison.Ordinal))
                .Should().BeEmpty("nothing about the profile changed");
        }

        // The control: the same route, with a different value, does say so.
        (await admin.PutAsJsonAsync($"/api/tenants/members/{member}", Edit(Branch("south"))))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        Names(await LastAuditAsync(slug, "tenant.member.updated", member), "profileChanged").Should().Equal("region");
    }

    [Fact]
    public async Task Adding_a_member_with_a_profile_is_audited_by_name()
    {
        var slug = await TenantAsync();
        var admin = await AdminOfAsync(slug);

        var added = await admin.PostAsJsonAsync("/api/tenants/members", new
        {
            email = NewEmail(),
            roleIds = new[] { SystemRoles.UserRoleId },
            profile = new Dictionary<string, string> { ["region"] = "north-branch-value" },
        });
        added.StatusCode.Should().Be(HttpStatusCode.OK);
        var userId = JsonDocument.Parse(await added.Content.ReadAsStringAsync())
            .RootElement.GetProperty("userId").GetGuid();

        var entry = await LastAuditAsync(slug, "tenant.member.added", userId);
        Names(entry, "profileAdded").Should().Equal("region");
        entry.GetRawText().Should().NotContain("north-branch-value");
    }

    [Fact]
    public async Task A_value_holding_a_control_character_is_refused_on_both_routes()
    {
        var slug = await TenantAsync();
        var admin = await AdminOfAsync(slug);
        var member = await MemberAsync(slug, Branch("south"));

        foreach (var value in new[] { "nor\u0000th", "nor\nth", "north\t" })
        {
            var profile = new Dictionary<string, string> { ["ward"] = value };

            var put = await admin.PutAsJsonAsync($"/api/tenants/members/{member}", Edit(profile));
            NotRateLimited(put);
            put.StatusCode.Should().Be(HttpStatusCode.BadRequest, "a control character is refused before the save");
            (await put.Content.ReadAsStringAsync()).Should().Contain("ward", "the answer names the attribute");

            var email = NewEmail();
            var post = await admin.PostAsJsonAsync("/api/tenants/members",
                new { email, roleIds = new[] { SystemRoles.UserRoleId }, profile });
            NotRateLimited(post);
            post.StatusCode.Should().Be(HttpStatusCode.BadRequest);

            using var scope = _factory.Services.CreateScope();
            var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
            (await session.Query<User>().Where(u => u.Email == email).ToListAsync())
                .Should().BeEmpty("a refused add invites nobody");
        }

        var stored = (await RowAsync(member, slug)).Profile;
        stored.Should().HaveCount(1);
        stored.Should().Contain("region", "south");
    }

    [Fact]
    public async Task A_member_without_the_capability_who_sends_a_bad_profile_is_refused_as_forbidden()
    {
        // The gate is a global pre-processor and FastEndpoints runs those when a request fails
        // binding or validation too, which RoleGateTests holds for every gated route with a body
        // that is not JSON. So the answer is the gate's, not the validator's, and it says nothing
        // about what a profile may hold.
        var slug = await TenantAsync();
        var member = await MemberAsync(slug, Branch("south"));

        var response = await ClientFor(member, slug, "User").PutAsJsonAsync($"/api/tenants/members/{member}",
            Edit(new Dictionary<string, string> { ["branch name"] = "" }));

        NotRateLimited(response);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var stored = (await RowAsync(member, slug)).Profile;
        stored.Should().HaveCount(1);
        stored.Should().Contain("region", "south", "refused either way, nothing is stored");
    }

    [Fact]
    public async Task The_roster_shows_each_members_profile_to_an_administrator()
    {
        var slug = await TenantAsync();
        var admin = await AdminOfAsync(slug);
        var member = await MemberAsync(slug, Branch("north"));

        var response = await admin.GetAsync("/api/tenants/members");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var rows = body.RootElement.GetProperty("items").EnumerateArray().ToList();
        rows.Should().HaveCount(2, "the administrator and the member");

        var row = rows.Single(r => r.GetProperty("userId").GetGuid() == member);
        row.GetProperty("profile").GetProperty("region").GetString().Should().Be("north");
    }
    // The profile hold check: a profile value a member's role reads widens that role, so the caller
    // must hold the value. The roles here read region and branches through a condition.

    private Task<Role> ConditionalRoleAsync(string slug, string attribute) =>
        ConditionalRoleWithReferenceAsync(slug, $"$CURRENT_USER.{attribute}");

    private async Task<Role> ConditionalRoleWithReferenceAsync(string slug, string reference)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var role = new Role
        {
            Id = Guid.NewGuid(), Name = $"cond-{Guid.NewGuid():N}"[..14], TenantSlug = slug,
            Permissions = [new ContentTypePermission
            {
                ContentTypeSlug = "ProfileHoldType",
                Read = new PermissionRule
                {
                    Enabled = true,
                    Conditions = new Dictionary<string, object>
                    {
                        ["Area"] = new Dictionary<string, object> { ["_eq"] = reference },
                    },
                },
            }],
        };
        session.Store(role);
        await session.SaveChangesAsync();
        return role;
    }

    /// <summary>A members manager who also holds the conditional role, with the profile given.</summary>
    private async Task<HttpClient> ManagerAsync(string slug, Role conditional, Dictionary<string, string> profile)
    {
        Role manager;
        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            manager = new Role
            {
                Id = Guid.NewGuid(), Name = $"mgr-{Guid.NewGuid():N}"[..14], TenantSlug = slug,
                SystemCapabilities = [SystemCapabilities.ManageTenantMembers],
            };
            session.Store(manager);
            await session.SaveChangesAsync();
        }

        var userId = await UserAsync();
        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new Membership
            {
                Id = Guid.NewGuid(), UserId = userId, TenantSlug = slug, Status = MembershipStatus.Active,
                RoleIds = [manager.Id, conditional.Id], Profile = profile,
            });
            await session.SaveChangesAsync();
        }

        return ClientFor(userId, slug, manager.Name);
    }

    private async Task<Guid> MemberWithRoleAsync(string slug, Role role, Dictionary<string, string> profile)
    {
        var userId = await UserAsync();
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new Membership
        {
            Id = Guid.NewGuid(), UserId = userId, TenantSlug = slug, Status = MembershipStatus.Active,
            RoleIds = [role.Id], Profile = profile,
        });
        await session.SaveChangesAsync();
        return userId;
    }

    private static object KeepRole(Role role, Dictionary<string, string> profile) =>
        new { roleIds = new[] { role.Id }, status = "Active", profile };

    [Fact]
    public async Task A_caller_who_does_not_hold_the_value_a_kept_role_reads_is_refused()
    {
        var slug = await TenantAsync();
        var role = await ConditionalRoleAsync(slug, "region");
        var manager = await ManagerAsync(slug, role, Branch("north"));
        var member = await MemberWithRoleAsync(slug, role, Branch("north"));

        var response = await manager.PutAsJsonAsync($"/api/tenants/members/{member}", KeepRole(role, Branch("south")));

        NotRateLimited(response);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, await response.Content.ReadAsStringAsync());
        (await response.Content.ReadAsStringAsync()).Should().Contain("'region'");
        (await RowAsync(member, slug)).Profile.Should().Contain("region", "north");
    }

    [Fact]
    public async Task A_caller_cannot_add_a_branch_they_do_not_hold_for_a_kept_role()
    {
        var slug = await TenantAsync();
        var role = await ConditionalRoleAsync(slug, "branches");
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var manager = await ManagerAsync(slug, role, new() { ["branches"] = a.ToString() });
        var member = await MemberWithRoleAsync(slug, role, new() { ["branches"] = a.ToString() });

        var refused = await manager.PutAsJsonAsync($"/api/tenants/members/{member}",
            KeepRole(role, new() { ["branches"] = $"{a},{b}" }));

        NotRateLimited(refused);
        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden, await refused.Content.ReadAsStringAsync());
        (await RowAsync(member, slug)).Profile["branches"].Should().Be(a.ToString());

        var narrowed = await manager.PutAsJsonAsync($"/api/tenants/members/{member}",
            KeepRole(role, new() { ["branches"] = a.ToString(), ["note"] = "x" }));
        narrowed.StatusCode.Should().Be(HttpStatusCode.OK, "a branch the caller holds widens nothing for them");
    }

    [Fact]
    public async Task A_caller_cannot_add_a_member_with_a_profile_they_do_not_hold()
    {
        var slug = await TenantAsync();
        var role = await ConditionalRoleAsync(slug, "region");
        var manager = await ManagerAsync(slug, role, Branch("north"));

        var refused = await manager.PostAsJsonAsync("/api/tenants/members", new
        {
            email = NewEmail(), roleIds = new[] { role.Id }, profile = Branch("south"),
        });
        NotRateLimited(refused);
        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden, await refused.Content.ReadAsStringAsync());

        var allowed = await manager.PostAsJsonAsync("/api/tenants/members", new
        {
            email = NewEmail(), roleIds = new[] { role.Id }, profile = Branch("north"),
        });
        allowed.StatusCode.Should().Be(HttpStatusCode.OK, await allowed.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_administrator_may_set_any_value_a_role_reads()
    {
        var slug = await TenantAsync();
        var role = await ConditionalRoleAsync(slug, "region");
        var admin = await AdminOfAsync(slug);
        var member = await MemberWithRoleAsync(slug, role, Branch("north"));

        var response = await admin.PutAsJsonAsync($"/api/tenants/members/{member}", KeepRole(role, Branch("south")));

        NotRateLimited(response);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        (await RowAsync(member, slug)).Profile.Should().Contain("region", "south");
    }

    [Fact]
    public async Task A_profile_change_no_role_reads_needs_no_hold()
    {
        var slug = await TenantAsync();
        var role = await ConditionalRoleAsync(slug, "region");
        var manager = await ManagerAsync(slug, role, Branch("north"));
        var member = await MemberWithRoleAsync(slug, role, Branch("north"));

        var response = await manager.PutAsJsonAsync($"/api/tenants/members/{member}",
            KeepRole(role, new() { ["region"] = "north", ["ward"] = "9" }));

        NotRateLimited(response);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        (await RowAsync(member, slug)).Profile.Should().Contain("ward", "9");
    }

    [Theory]
    [InlineData("$CURRENT_USER.branch")]
    [InlineData("$CURRENT_BRANCH")]
    [InlineData("$CURRENT_USER.branches")]
    public async Task Every_form_of_the_branch_reference_reads_branches(string reference)
    {
        var slug = await TenantAsync();
        var role = await ConditionalRoleWithReferenceAsync(slug, reference);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var manager = await ManagerAsync(slug, role, new() { ["branches"] = a.ToString() });
        var member = await MemberWithRoleAsync(slug, role, new() { ["branches"] = a.ToString() });

        var response = await manager.PutAsJsonAsync($"/api/tenants/members/{member}",
            KeepRole(role, new() { ["branches"] = $"{a},{b}" }));

        NotRateLimited(response);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, await response.Content.ReadAsStringAsync());
        (await RowAsync(member, slug)).Profile["branches"].Should().Be(a.ToString());
    }

    [Fact]
    public async Task Branches_are_held_even_when_no_role_of_the_member_mentions_them()
    {
        var slug = await TenantAsync();
        var role = await ConditionalRoleAsync(slug, "region");
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var manager = await ManagerAsync(slug, role, new() { ["branches"] = a.ToString() });
        var member = await MemberAsync(slug);

        var refused = await manager.PutAsJsonAsync($"/api/tenants/members/{member}", new
        {
            roleIds = new[] { SystemRoles.UserRoleId }, status = "Active", profile = new Dictionary<string, string> { ["branches"] = b.ToString() },
        });
        NotRateLimited(refused);
        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden, await refused.Content.ReadAsStringAsync());

        var allowed = await manager.PutAsJsonAsync($"/api/tenants/members/{member}", new
        {
            roleIds = new[] { SystemRoles.UserRoleId }, status = "Active", profile = new Dictionary<string, string> { ["branches"] = a.ToString() },
        });
        allowed.StatusCode.Should().Be(HttpStatusCode.OK, await allowed.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_role_granted_now_needs_every_value_it_reads_held_even_if_unchanged()
    {
        var slug = await TenantAsync();
        var role = await ConditionalRoleAsync(slug, "region");
        var manager = await ManagerAsync(slug, role, Branch("north"));
        var member = await MemberAsync(slug, Branch("south"));

        var response = await manager.PutAsJsonAsync($"/api/tenants/members/{member}", new
        {
            roleIds = new[] { SystemRoles.UserRoleId, role.Id }, status = "Active", profile = Branch("south"),
        });

        NotRateLimited(response);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, await response.Content.ReadAsStringAsync());
        (await RowAsync(member, slug)).RoleIds.Should().NotContain(role.Id);
    }

    [Fact]
    public async Task Setting_a_value_then_granting_the_role_that_reads_it_is_refused_at_the_second_step()
    {
        var slug = await TenantAsync();
        var role = await ConditionalRoleAsync(slug, "region");
        var manager = await ManagerAsync(slug, role, Branch("north"));
        var member = await MemberAsync(slug);

        var first = await manager.PutAsJsonAsync($"/api/tenants/members/{member}", new
        {
            roleIds = new[] { SystemRoles.UserRoleId }, status = "Active", profile = Branch("south"),
        });
        first.StatusCode.Should().Be(HttpStatusCode.OK, "no role of the member reads region yet");

        var second = await manager.PutAsJsonAsync($"/api/tenants/members/{member}", new
        {
            roleIds = new[] { SystemRoles.UserRoleId, role.Id }, status = "Active",
        });
        NotRateLimited(second);
        second.StatusCode.Should().Be(HttpStatusCode.Forbidden, await second.Content.ReadAsStringAsync());
        (await RowAsync(member, slug)).RoleIds.Should().NotContain(role.Id);
    }

    [Fact]
    public async Task Adding_an_existing_member_with_a_new_role_and_no_profile_checks_the_stored_profile()
    {
        var slug = await TenantAsync();
        var role = await ConditionalRoleAsync(slug, "region");
        var manager = await ManagerAsync(slug, role, Branch("north"));
        var email = NewEmail();
        var member = await MemberAsync(slug, Branch("south"), email);

        var response = await manager.PostAsJsonAsync("/api/tenants/members", new
        {
            email, roleIds = new[] { SystemRoles.UserRoleId, role.Id },
        });

        NotRateLimited(response);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, await response.Content.ReadAsStringAsync());
        (await RowAsync(member, slug)).RoleIds.Should().NotContain(role.Id);
    }

    [Fact]
    public async Task A_role_held_through_a_group_counts_and_an_empty_role_list_does_not_skip_the_check()
    {
        var slug = await TenantAsync();
        var role = await ConditionalRoleAsync(slug, "region");
        var manager = await ManagerAsync(slug, role, Branch("north"));
        var member = await MemberAsync(slug, Branch("north"));
        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new UserGroup
            {
                Id = Guid.NewGuid(), Name = $"grp-{Guid.NewGuid():N}"[..12], UserIds = [member], RoleIds = [role.Id],
            });
            await session.SaveChangesAsync();
        }

        var response = await manager.PutAsJsonAsync($"/api/tenants/members/{member}", new
        {
            roleIds = Array.Empty<Guid>(), status = "Active", profile = Branch("south"),
        });

        NotRateLimited(response);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, await response.Content.ReadAsStringAsync());
        (await RowAsync(member, slug)).Profile.Should().Contain("region", "north");
    }

    [Fact]
    public async Task A_value_that_differs_only_by_padding_is_not_held()
    {
        var slug = await TenantAsync();
        var role = await ConditionalRoleAsync(slug, "region");
        var manager = await ManagerAsync(slug, role, Branch("north"));
        var member = await MemberWithRoleAsync(slug, role, Branch("north"));

        var response = await manager.PutAsJsonAsync($"/api/tenants/members/{member}", KeepRole(role, Branch("north ")));

        NotRateLimited(response);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Adding_a_suspended_member_with_a_new_role_and_no_profile_checks_the_stored_profile()
    {
        var slug = await TenantAsync();
        var role = await ConditionalRoleAsync(slug, "region");
        var manager = await ManagerAsync(slug, role, Branch("north"));
        var email = NewEmail();
        var member = await MemberAsync(slug, Branch("south"), email);
        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var row = await session.Query<Membership>().FirstAsync(m => m.UserId == member && m.TenantSlug == slug);
            row.Status = MembershipStatus.Suspended;
            session.Store(row);
            await session.SaveChangesAsync();
        }

        var response = await manager.PostAsJsonAsync("/api/tenants/members", new
        {
            email, roleIds = new[] { SystemRoles.UserRoleId, role.Id },
        });

        NotRateLimited(response);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, await response.Content.ReadAsStringAsync());
        var after = await RowAsync(member, slug);
        after.Status.Should().Be(MembershipStatus.Suspended);
        after.RoleIds.Should().NotContain(role.Id);
    }
}
