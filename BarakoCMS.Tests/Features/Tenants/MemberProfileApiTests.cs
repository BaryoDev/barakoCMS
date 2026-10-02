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
/// A rule such as <c>Branch eq $CURRENT_USER.branch</c> trusts the profile, so writing a profile is
/// granting access. The only writers are the two member routes, behind the capability that already
/// lets its holder assign roles in the tenant. A member changing their own attribute, or an
/// administrator of one tenant changing a profile in another, would each be a way to grant what
/// the caller could not grant before.
///
/// Requests carry their own client IP and a tenant claim for the reasons
/// <see cref="TenantMemberApiTests"/> gives.
/// </remarks>
[Collection("Sequential")]
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
        session.Store(new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = slug, IsActive = true });
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

    private static Dictionary<string, string> Branch(string value) => new() { ["branch"] = value };

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
            Edit(new Dictionary<string, string> { ["branch"] = "north", ["ward"] = "7" }));

        NotRateLimited(response);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        var stored = (await RowAsync(member, slug)).Profile;
        stored.Should().HaveCount(2);
        stored.Should().Contain("branch", "north").And.Contain("ward", "7");

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("profile").GetProperty("branch").GetString().Should().Be("north");
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
        stored.Should().Contain("branch", "south");
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
        stored.Should().Contain("branch", "north");
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
        stored.Should().Contain("branch", "south", "the attribute a rule trusts is not the member's to set");

        // The control: the route exists and the refusal was about who asked.
        var admin = await AdminOfAsync(slug);
        var allowed = await admin.PutAsJsonAsync($"/api/tenants/members/{member}", Edit(Branch("north")));
        allowed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await RowAsync(member, slug)).Profile.Should().Contain("branch", "north");
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
        (await RowAsync(userId, slug)).Profile.Should().Contain("branch", "south");
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
        stored.Should().Contain("branch", "south", "their membership is untouched");

        // The control: the same client and body, against a member of its own tenant.
        var ours = await MemberAsync(mine, Branch("south"));
        var allowed = await admin.PutAsJsonAsync($"/api/tenants/members/{ours}", Edit(Branch("north")));
        allowed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await RowAsync(ours, mine)).Profile.Should().Contain("branch", "north");
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

        (await RowAsync(person, mine)).Profile.Should().Contain("branch", "north");
        (await RowAsync(person, theirs)).Profile.Should().Contain("branch", "south",
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
            ("an empty value", new Dictionary<string, string> { ["branch"] = "" }),
            ("a value of only spaces", new Dictionary<string, string> { ["branch"] = "   " }),
            ("a value past the limit", new Dictionary<string, string> { ["branch"] = new string('n', 257) }),
            ("two names differing only by case", new Dictionary<string, string> { ["branch"] = "north", ["Branch"] = "north" }),
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
        stored.Should().Contain("branch", "south", "a refused request changes nothing");
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
        (await RowAsync(userId, slug)).Profile.Should().Contain("branch", "north");

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
        read.Profile.Should().Contain("branch", "north", "the read has to be the stale one");

        // Another clears the profile, and that commits first.
        var cleared = await admin.PutAsJsonAsync($"/api/tenants/members/{member}",
            Edit(new Dictionary<string, string>()));
        cleared.StatusCode.Should().Be(HttpStatusCode.OK);
        (await RowAsync(member, slug)).Profile.Should().BeEmpty();

        // The first one's write, which is what both member routes queue for a request with no profile.
        global::barakoCMS.Features.Tenants.Members.Members.QueueWrite(
            session, read, [SystemRoles.HRRoleId], MembershipStatus.Active, profile: null);
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
            new { email, roleIds = new[] { SystemRoles.HRRoleId } });
        NotRateLimited(again);
        again.StatusCode.Should().Be(HttpStatusCode.OK, await again.Content.ReadAsStringAsync());

        var row = await RowAsync(member, slug);
        row.RoleIds.Should().Equal(SystemRoles.HRRoleId);
        row.Profile.Should().HaveCount(1);
        row.Profile.Should().Contain("branch", "north", "a client that knows nothing of profiles must not erase one");

        using (var body = JsonDocument.Parse(await again.Content.ReadAsStringAsync()))
            body.RootElement.GetProperty("profile").GetProperty("branch").GetString().Should().Be("north");

        // And one that does send a profile replaces it.
        var replaced = await admin.PostAsJsonAsync("/api/tenants/members",
            new { email, roleIds = new[] { SystemRoles.HRRoleId }, profile = Branch("south") });
        replaced.StatusCode.Should().Be(HttpStatusCode.OK);
        (await RowAsync(member, slug)).Profile.Should().Contain("branch", "south");
    }

    [Fact]
    public async Task The_audit_entry_names_what_changed_in_a_profile_and_never_a_value()
    {
        var slug = await TenantAsync();
        var admin = await AdminOfAsync(slug);
        var member = await MemberAsync(slug, new Dictionary<string, string>
        {
            ["branch"] = "north-branch-value",
            ["ward"] = "ward-seven-value",
        });

        var changed = await admin.PutAsJsonAsync($"/api/tenants/members/{member}", Edit(new Dictionary<string, string>
        {
            ["branch"] = "south-branch-value",
            ["level"] = "level-three-value",
        }));
        NotRateLimited(changed);
        changed.StatusCode.Should().Be(HttpStatusCode.OK);

        var entry = await LastAuditAsync(slug, "tenant.member.updated", member);
        Names(entry, "profileAdded").Should().Equal("level");
        Names(entry, "profileRemoved").Should().Equal("ward");
        Names(entry, "profileChanged").Should().Equal("branch");

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
        Names(await LastAuditAsync(slug, "tenant.member.updated", member), "profileChanged").Should().Equal("branch");
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
            profile = new Dictionary<string, string> { ["branch"] = "north-branch-value" },
        });
        added.StatusCode.Should().Be(HttpStatusCode.OK);
        var userId = JsonDocument.Parse(await added.Content.ReadAsStringAsync())
            .RootElement.GetProperty("userId").GetGuid();

        var entry = await LastAuditAsync(slug, "tenant.member.added", userId);
        Names(entry, "profileAdded").Should().Equal("branch");
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
        stored.Should().Contain("branch", "south");
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
        stored.Should().Contain("branch", "south", "refused either way, nothing is stored");
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
        row.GetProperty("profile").GetProperty("branch").GetString().Should().Be("north");
    }
}
