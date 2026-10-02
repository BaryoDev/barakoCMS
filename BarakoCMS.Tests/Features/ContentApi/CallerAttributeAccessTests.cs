using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.ContentApi;

/// <summary>
/// A rule that compares a field against the caller's member profile, through the API.
/// </summary>
/// <remarks>
/// A branch manager reads their branch's orders, and nobody created those orders for them, so
/// <c>$createdBy</c> cannot express it. The rule is <c>Branch eq $CURRENT_USER.branch</c>.
///
/// The cases that matter are the ones where the caller has no value: no membership, no attribute,
/// a profile in some other tenant, a suspended membership. Most of them use <c>_ne</c>, because a
/// variable that is not resolved is plain text, and every entry is "not equal" to that text. Under
/// <c>_eq</c> the same mistake denies and looks correct.
/// </remarks>
[Collection("Sequential")]
public class CallerAttributeAccessTests
{
    private readonly IntegrationTestFixture _factory;

    public CallerAttributeAccessTests(IntegrationTestFixture factory) => _factory = factory;

    private static string NewType() => $"branchorder{Guid.NewGuid():n}"[..24];

    private static PermissionRule Rule(string op, params (string Field, string Op, object Expected)[] also)
    {
        var conditions = new Dictionary<string, object>
        {
            ["Branch"] = new Dictionary<string, object> { [op] = "$CURRENT_USER.branch" },
        };

        foreach (var (field, extraOp, expected) in also)
            conditions[field] = new Dictionary<string, object> { [extraOp] = expected };

        return new PermissionRule { Enabled = true, Conditions = conditions };
    }

    /// <summary>A user whose one role reads and updates the type under the rule given.</summary>
    private async Task<(HttpClient Client, Guid UserId)> MemberAsync(string type, Func<PermissionRule> rule)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = $"Branch_{Guid.NewGuid():n}",
            Permissions =
            [
                new ContentTypePermission
                {
                    ContentTypeSlug = type,
                    Read = rule(),
                    Update = rule(),
                },
            ],
        };
        session.Store(role);

        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = $"member_{Guid.NewGuid():n}",
            Email = $"member_{Guid.NewGuid():n}@example.com",
            RoleIds = [role.Id],
        };
        session.Store(user);
        await session.SaveChangesAsync();

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(roles: [role.Name], userId: user.Id.ToString()));

        return (client, user.Id);
    }

    /// <summary>Writes the user's membership in a tenant, replacing the profile and status it had.</summary>
    private async Task ProfileAsync(
        Guid userId,
        Dictionary<string, string>? profile,
        string slug = Tenant.DefaultSlug,
        MembershipStatus status = MembershipStatus.Active)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var membership = await session.Query<Membership>()
            .FirstOrDefaultAsync(m => m.UserId == userId && m.TenantSlug == slug)
            ?? new Membership { Id = Guid.NewGuid(), UserId = userId, TenantSlug = slug };

        membership.Status = status;
        membership.Profile = profile!;
        session.Store(membership);
        await session.SaveChangesAsync();
    }

    private static Dictionary<string, string> Branch(string value) => new() { ["branch"] = value };

    private async Task<HttpClient> SuperAdminAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = $"admin_{Guid.NewGuid():n}",
            Email = $"admin_{Guid.NewGuid():n}@example.com",
            RoleIds = [SystemRoles.SuperAdminRoleId],
        };
        session.Store(user);
        await session.SaveChangesAsync();

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(roles: ["SuperAdmin"], userId: user.Id.ToString()));
        return client;
    }

    private static async Task<Guid> CreateAsync(HttpClient admin, string type, string title, string? branch)
    {
        var data = new Dictionary<string, object> { ["Title"] = title };
        if (branch is not null) data["Branch"] = branch;

        var res = await admin.PostAsJsonAsync("/api/contents", new { contentType = type, data });
        res.IsSuccessStatusCode.Should().BeTrue("creating an entry returned {0}: {1}",
            res.StatusCode, await res.Content.ReadAsStringAsync());

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("id").GetGuid();
    }

    /// <summary>Two north entries, three south ones and one with no branch at all.</summary>
    private async Task<(List<Guid> North, List<Guid> South, Guid Unbranched)> SeedAsync(string type)
    {
        var admin = await SuperAdminAsync();

        var north = new List<Guid>();
        var south = new List<Guid>();

        foreach (var i in Enumerable.Range(0, 2)) north.Add(await CreateAsync(admin, type, $"north {i}", "north"));
        foreach (var i in Enumerable.Range(0, 3)) south.Add(await CreateAsync(admin, type, $"south {i}", "south"));
        var unbranched = await CreateAsync(admin, type, "no branch", null);

        // The control every denial below leans on: the entries exist and a caller who may read
        // them gets all six, so an empty list from a member is a refusal and not an empty type.
        (await ListedAsync(admin, type)).Should().HaveCount(6);

        return (north, south, unbranched);
    }

    /// <summary>What the caller can list of one type, or of every type when none is named.</summary>
    private static async Task<List<Guid>> ListedAsync(HttpClient client, string? type)
    {
        var res = await client.GetAsync(type is null
            ? "/api/contents?pageSize=50"
            : $"/api/contents?contentType={type}&pageSize=50");
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var ids = doc.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("id").GetGuid())
            .ToList();

        doc.RootElement.GetProperty("totalItems").GetInt32().Should().Be(ids.Count,
            "the total counts what the caller may read, and one page holds all of it here");

        return ids;
    }

    private static async Task<HttpStatusCode> GetAsync(HttpClient client, Guid id) =>
        (await client.GetAsync($"/api/contents/{id}")).StatusCode;

    [Fact]
    public async Task A_member_reads_an_entry_of_their_branch_and_is_refused_another_branchs()
    {
        var type = NewType();
        var (north, south, unbranched) = await SeedAsync(type);
        var (client, userId) = await MemberAsync(type, () => Rule("_eq"));
        await ProfileAsync(userId, Branch("north"));

        (await GetAsync(client, north[0])).Should().Be(HttpStatusCode.OK);
        (await GetAsync(client, south[0])).Should().Be(HttpStatusCode.Forbidden);
        (await GetAsync(client, unbranched)).Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_list_returns_only_the_callers_branch()
    {
        var type = NewType();
        var (north, south, _) = await SeedAsync(type);
        var (client, userId) = await MemberAsync(type, () => Rule("_eq"));

        await ProfileAsync(userId, Branch("north"));
        var asNorth = await ListedAsync(client, type);
        asNorth.Should().HaveCount(2);
        asNorth.Should().BeEquivalentTo(north);

        await ProfileAsync(userId, Branch("south"));
        var asSouth = await ListedAsync(client, type);
        asSouth.Should().HaveCount(3);
        asSouth.Should().BeEquivalentTo(south);
    }

    [Fact]
    public async Task A_list_the_database_cannot_page_returns_only_the_callers_branch()
    {
        // A number inside a list is one of the shapes the predicate compiler declines, so this rule
        // is answered per entry by the evaluator. It is true for every entry here.
        var type = NewType();
        var (north, _, _) = await SeedAsync(type);
        var (client, userId) = await MemberAsync(type,
            () => Rule("_eq", ("Title", "_nin", new List<object> { 42L })));
        await ProfileAsync(userId, Branch("north"));

        var listed = await ListedAsync(client, type);

        listed.Should().HaveCount(2);
        listed.Should().BeEquivalentTo(north);
    }

    [Fact]
    public async Task A_caller_with_no_membership_matches_nothing_even_under_not_equal()
    {
        var type = NewType();
        var (north, south, _) = await SeedAsync(type);
        var (client, _) = await MemberAsync(type, () => Rule("_ne"));

        (await ListedAsync(client, type)).Should().BeEmpty("not equal to no branch is not every branch");
        (await GetAsync(client, north[0])).Should().Be(HttpStatusCode.Forbidden);
        (await GetAsync(client, south[0])).Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_member_without_the_attribute_matches_nothing_even_under_not_equal()
    {
        var type = NewType();
        var (north, _, _) = await SeedAsync(type);
        var (client, userId) = await MemberAsync(type, () => Rule("_ne"));

        foreach (var profile in new[]
                 {
                     new Dictionary<string, string>(),
                     new Dictionary<string, string> { ["ward"] = "north" },
                     new Dictionary<string, string> { ["Branch"] = "south" },
                     new Dictionary<string, string> { ["branch"] = "" },
                     null,
                 })
        {
            await ProfileAsync(userId, profile);

            (await ListedAsync(client, type)).Should().BeEmpty(
                "a profile of {0} holds no branch", profile is null ? "null" : JsonSerializer.Serialize(profile));
            (await GetAsync(client, north[0])).Should().Be(HttpStatusCode.Forbidden);
        }

        // The control: the same caller, once they have a branch, sees the other branches' entries
        // and the one with no branch is still left out, because the rule cannot read a field the
        // entry does not have.
        await ProfileAsync(userId, Branch("north"));
        (await ListedAsync(client, type)).Should().HaveCount(3);
    }

    [Fact]
    public async Task A_list_of_every_type_is_answered_per_entry_and_gives_a_member_without_the_attribute_nothing()
    {
        // With no contentType the list asks for no predicate and puts every entry through the
        // evaluator, so this is the per-entry path with the variable unresolved. Naming the type
        // would not do: the compiler answers FALSE for an unresolved variable before it looks at
        // the rest of the rule, and the per-entry path would never run.
        var type = NewType();
        var (_, south, _) = await SeedAsync(type);
        var (client, userId) = await MemberAsync(type, () => Rule("_ne"));

        await ProfileAsync(userId, new Dictionary<string, string> { ["ward"] = "north" });
        (await ListedAsync(client, null)).Should().BeEmpty("the member has no branch, and reads no other type");

        // The control: the same list, once they have a branch, is the three entries of the other one.
        await ProfileAsync(userId, Branch("north"));
        var listed = await ListedAsync(client, null);
        listed.Should().HaveCount(3);
        listed.Should().BeEquivalentTo(south);
    }

    [Fact]
    public async Task A_changed_attribute_applies_to_the_next_request()
    {
        var type = NewType();
        var (north, south, _) = await SeedAsync(type);
        var (client, userId) = await MemberAsync(type, () => Rule("_eq"));

        await ProfileAsync(userId, Branch("north"));
        (await GetAsync(client, north[0])).Should().Be(HttpStatusCode.OK);
        (await GetAsync(client, south[0])).Should().Be(HttpStatusCode.Forbidden);

        // Same token throughout. Nothing about the profile is in it.
        await ProfileAsync(userId, Branch("south"));
        (await GetAsync(client, north[0])).Should().Be(HttpStatusCode.Forbidden, "the grant went with the attribute");
        (await GetAsync(client, south[0])).Should().Be(HttpStatusCode.OK);

        await ProfileAsync(userId, new Dictionary<string, string>());
        (await GetAsync(client, south[0])).Should().Be(HttpStatusCode.Forbidden);
        (await ListedAsync(client, type)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_profile_in_another_tenant_does_not_apply_in_this_one()
    {
        var type = NewType();
        var (north, _, _) = await SeedAsync(type);
        var (client, userId) = await MemberAsync(type, () => Rule("_eq"));

        await ProfileAsync(userId, Branch("north"), slug: $"other-{Guid.NewGuid():n}"[..16]);

        (await GetAsync(client, north[0])).Should().Be(HttpStatusCode.Forbidden,
            "the branch they hold is in another tenant");
        (await ListedAsync(client, type)).Should().BeEmpty();

        await ProfileAsync(userId, Branch("north"));
        (await GetAsync(client, north[0])).Should().Be(HttpStatusCode.OK, "the same value in this tenant is the control");
    }

    [Fact]
    public async Task A_suspended_membership_lends_nothing_from_its_profile()
    {
        var type = NewType();
        var (north, _, _) = await SeedAsync(type);
        var (client, userId) = await MemberAsync(type, () => Rule("_eq"));

        await ProfileAsync(userId, Branch("north"), status: MembershipStatus.Suspended);
        (await GetAsync(client, north[0])).Should().Be(HttpStatusCode.Forbidden);

        await ProfileAsync(userId, Branch("north"), status: MembershipStatus.Removed);
        (await GetAsync(client, north[0])).Should().Be(HttpStatusCode.Forbidden);

        await ProfileAsync(userId, Branch("north"));
        (await GetAsync(client, north[0])).Should().Be(HttpStatusCode.OK, "active again is the control");
    }

    [Fact]
    public async Task An_update_is_allowed_only_within_the_callers_branch()
    {
        var type = NewType();
        var (north, south, _) = await SeedAsync(type);
        var (client, userId) = await MemberAsync(type, () => Rule("_eq"));
        await ProfileAsync(userId, Branch("north"));

        var theirs = await client.PutAsJsonAsync($"/api/contents/{south[0]}", new
        {
            data = new Dictionary<string, object> { ["Title"] = "taken over", ["Branch"] = "south" },
        });
        theirs.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var mine = await client.PutAsJsonAsync($"/api/contents/{north[0]}", new
        {
            data = new Dictionary<string, object> { ["Title"] = "edited", ["Branch"] = "north" },
        });
        mine.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}",
            mine.StatusCode, await mine.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_member_with_no_branch_cannot_update_under_not_equal()
    {
        var type = NewType();
        var (north, _, _) = await SeedAsync(type);
        var (client, userId) = await MemberAsync(type, () => Rule("_ne"));
        await ProfileAsync(userId, new Dictionary<string, string>());

        var update = await client.PutAsJsonAsync($"/api/contents/{north[0]}", new
        {
            data = new Dictionary<string, object> { ["Title"] = "taken over", ["Branch"] = "north" },
        });

        update.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
