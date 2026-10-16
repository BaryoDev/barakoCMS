using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

[Collection("Sequential")]
public class InviteMemberTests
{
    private readonly IntegrationTestFixture _fixture;
    private static int _ip;

    public InviteMemberTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record InviteHarness(
        HttpClient Client,
        Guid MemberRoleId,
        Guid PromotedRoleId,
        Guid OtherTenantRoleId,
        Guid SelfUserId,
        Guid OtherUserId,
        Func<string, Task<List<Guid>>> RolesOfAsync,
        Func<string, Task<bool>> UserExistsAsync,
        Func<string, Dictionary<string, string>?, Task<(Guid UserId, List<Guid> RoleIds)>> AddOfficerAsync,
        Func<string, Dictionary<string, string>?, Task> AddAndRemoveAsync,
        Func<Task> RevokeAsync);

    private async Task StoreAsync<T>(params T[] documents) where T : notnull
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(documents);
        await session.SaveChangesAsync(Ct);
    }

    private HttpClient ClientFor(string slug, string roleName, Guid userId)
    {
        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _fixture.CreateToken(
            [roleName], userId.ToString(), new Dictionary<string, string> { ["tenant"] = slug }));
        client.DefaultRequestHeaders.Add("X-Tenant", slug);
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, $"2001:db8:a11::{Interlocked.Increment(ref _ip):x}");
        return client;
    }

    private async Task<Guid> UserAsync(string? email = null)
    {
        var id = Guid.NewGuid();
        await StoreAsync(new User
        {
            Id = id, Username = $"iv{id:N}"[..20], Email = email ?? $"iv{id:N}@example.com", PasswordHash = string.Empty,
        });
        return id;
    }

    private async Task<InviteHarness> InviterAsync(bool holdsCapability)
    {
        var slug = $"iv-{Guid.NewGuid():N}"[..16];
        var other = $"io-{Guid.NewGuid():N}"[..16];
        await StoreAsync(
            new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = slug },
            new Tenant { Id = Guid.NewGuid(), Slug = other, Name = other });
        _fixture.Services.GetRequiredService<ITenantDomainSource>().Invalidate();

        var inviterRole = new Role
        {
            Id = Guid.NewGuid(), Name = $"inv-{Guid.NewGuid():N}"[..14], TenantSlug = slug,
            SystemCapabilities = holdsCapability ? [SystemCapabilities.InviteTenantMembers] : [],
        };
        var member = new Role { Id = Guid.NewGuid(), Name = "Member", TenantSlug = slug, Invitable = true };
        var promoted = new Role
        {
            Id = Guid.NewGuid(), Name = "Promoted", TenantSlug = slug, SystemCapabilities = [SystemCapabilities.ViewSensitive],
        };
        var otherTenantRole = new Role { Id = Guid.NewGuid(), Name = "Member", TenantSlug = other, Invitable = true };
        await StoreAsync(inviterRole, member, promoted, otherTenantRole);

        var self = await UserAsync();
        var otherUser = await UserAsync();
        await StoreAsync(
            new Membership { Id = Guid.NewGuid(), UserId = self, TenantSlug = slug, Status = MembershipStatus.Active, RoleIds = [inviterRole.Id] },
            new Membership { Id = Guid.NewGuid(), UserId = otherUser, TenantSlug = slug, Status = MembershipStatus.Active, RoleIds = [member.Id] });

        var client = ClientFor(slug, inviterRole.Name, self);
        var adminUser = await UserAsync();
        await StoreAsync(new Membership
        {
            Id = Guid.NewGuid(), UserId = adminUser, TenantSlug = slug, Status = MembershipStatus.Active, RoleIds = [SystemRoles.AdminRoleId],
        });
        var admin = ClientFor(slug, "Admin", adminUser);

        async Task<List<Guid>> RolesOfAsync(string email)
        {
            using var scope = _fixture.Services.CreateScope();
            var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
            var user = await session.Query<User>().FirstAsync(u => u.NormalizedEmail == email.ToLowerInvariant(), Ct);
            var membership = await session.Query<Membership>().FirstAsync(m => m.UserId == user.Id && m.TenantSlug == slug, Ct);
            return membership.RoleIds;
        }

        async Task<bool> UserExistsAsync(string email)
        {
            using var scope = _fixture.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<IQuerySession>()
                .Query<User>().AnyAsync(u => u.NormalizedEmail == email.ToLowerInvariant(), Ct);
        }

        async Task<(Guid UserId, List<Guid> RoleIds)> AddOfficerAsync(string email, Dictionary<string, string>? profile)
        {
            var id = await UserAsync(email);
            await StoreAsync(new Membership
            {
                Id = Guid.NewGuid(), UserId = id, TenantSlug = slug, Status = MembershipStatus.Active,
                RoleIds = [promoted.Id], Profile = profile ?? new(),
            });
            return (id, [promoted.Id]);
        }

        async Task AddAndRemoveAsync(string email, Dictionary<string, string>? profile)
        {
            var added = await admin.PostAsJsonAsync("/api/tenants/members",
                new { Email = email, RoleIds = new[] { member.Id }, Profile = profile }, Ct);
            added.StatusCode.Should().Be(HttpStatusCode.OK, await added.Content.ReadAsStringAsync(Ct));
            var userId = (await added.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("userId").GetGuid();
            (await admin.DeleteAsync($"/api/tenants/members/{userId}", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        async Task RevokeAsync()
        {
            using (var scope = _fixture.Services.CreateScope())
            {
                var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
                var role = await session.LoadAsync<Role>(inviterRole.Id, Ct);
                role!.SystemCapabilities = [];
                session.Store(role);
                await session.SaveChangesAsync(Ct);
                scope.ServiceProvider.GetRequiredService<IPermissionResolver>().InvalidateAllPermissions();
            }
        }

        return new InviteHarness(client, member.Id, promoted.Id, otherTenantRole.Id, self, otherUser,
            RolesOfAsync, UserExistsAsync, AddOfficerAsync, AddAndRemoveAsync, RevokeAsync);
    }

    [Fact]
    public async Task A_holder_invites_a_new_address_and_the_member_gets_only_the_invitable_role()
    {
        var h = await InviterAsync(holdsCapability: true);
        var resp = await h.Client.PostAsJsonAsync("/api/tenants/members/invite", new { Email = "new@example.test" }, Ct);
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync(Ct));
        (await h.RolesOfAsync("new@example.test")).Should().Equal(h.MemberRoleId);
    }

    [Fact]
    public async Task A_caller_without_the_capability_gets_403_and_nothing_is_created()
    {
        var h = await InviterAsync(holdsCapability: false);
        (await h.Client.PostAsJsonAsync("/api/tenants/members/invite", new { Email = "no@example.test" }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await h.UserExistsAsync("no@example.test")).Should().BeFalse();
    }

    [Fact]
    public async Task Asking_for_a_role_that_is_not_invitable_is_refused_and_creates_nothing()
    {
        var h = await InviterAsync(holdsCapability: true);
        foreach (var id in new[] { h.PromotedRoleId, SystemRoles.SuperAdminRoleId, Guid.NewGuid(), h.OtherTenantRoleId })
        {
            var resp = await h.Client.PostAsJsonAsync("/api/tenants/members/invite",
                new { Email = "up@example.test", RoleIds = new[] { id } }, Ct);
            resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, id.ToString());
        }

        (await h.UserExistsAsync("up@example.test")).Should().BeFalse();
    }

    [Fact]
    public async Task Inviting_an_active_member_changes_nothing()
    {
        var h = await InviterAsync(holdsCapability: true);
        var officer = await h.AddOfficerAsync("boss@example.test", null);
        var resp = await h.Client.PostAsJsonAsync("/api/tenants/members/invite", new { Email = "boss@example.test" }, Ct);
        (await resp.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("status").GetString().Should().Be("existing");
        (await h.RolesOfAsync("boss@example.test")).Should().BeEquivalentTo(officer.RoleIds);
    }

    [Fact]
    public async Task A_removed_member_is_not_brought_back_by_an_invite()
    {
        var h = await InviterAsync(holdsCapability: true);
        await h.AddAndRemoveAsync("gone@example.test", null);
        (await h.Client.PostAsJsonAsync("/api/tenants/members/invite", new { Email = "gone@example.test" }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task A_profile_value_held_by_another_active_member_is_refused_and_creates_nothing()
    {
        var h = await InviterAsync(holdsCapability: true);
        await h.AddOfficerAsync("holder@example.test", new() { ["memberNo"] = "N-77" });
        var clash = await h.Client.PostAsJsonAsync("/api/tenants/members/invite",
            new { Email = "stooge@example.test", Profile = new { memberNo = " n-77 " } }, Ct);
        clash.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await h.UserExistsAsync("stooge@example.test")).Should().BeFalse();
        (await h.Client.PostAsJsonAsync("/api/tenants/members/invite",
            new { Email = "honest@example.test", Profile = new { memberNo = "N-78" } }, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_removed_members_profile_value_does_not_block_and_a_re_invite_is_not_a_clash_with_itself()
    {
        var h = await InviterAsync(holdsCapability: true);
        await h.AddAndRemoveAsync("old@example.test", new() { ["memberNo"] = "N-80" });
        (await h.Client.PostAsJsonAsync("/api/tenants/members/invite",
            new { Email = "fresh@example.test", Profile = new { memberNo = "N-80" } }, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        var again = await h.Client.PostAsJsonAsync("/api/tenants/members/invite",
            new { Email = "fresh@example.test", Profile = new { memberNo = "N-80" } }, Ct);
        (await again.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("status").GetString().Should().Be("existing");
    }

    [Fact]
    public async Task Taking_the_capability_away_takes_effect_on_the_next_request_with_the_same_token()
    {
        var h = await InviterAsync(holdsCapability: true);
        (await h.Client.PostAsJsonAsync("/api/tenants/members/invite", new { Email = "a@example.test" }, Ct)).IsSuccessStatusCode.Should().BeTrue();
        await h.RevokeAsync();
        (await h.Client.PostAsJsonAsync("/api/tenants/members/invite", new { Email = "b@example.test" }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_capability_opens_no_other_member_route()
    {
        var h = await InviterAsync(holdsCapability: true);
        (await h.Client.GetAsync("/api/tenants/members", Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await h.Client.PostAsJsonAsync("/api/tenants/members", new { Email = "x@example.test", RoleIds = new[] { h.PromotedRoleId } }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await h.Client.PutAsJsonAsync($"/api/tenants/members/{h.SelfUserId}", new { RoleIds = new[] { h.PromotedRoleId }, Status = "Active" }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await h.Client.DeleteAsync($"/api/tenants/members/{h.OtherUserId}", Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_get_lists_only_invitable_roles_and_is_gated_the_same_way()
    {
        var h = await InviterAsync(holdsCapability: true);
        var json = await h.Client.GetFromJsonAsync<JsonElement>("/api/tenants/members/invite", Ct);
        json.GetProperty("roles").EnumerateArray().Select(r => r.GetProperty("id").GetGuid()).Should().Equal(h.MemberRoleId);
        var no = await InviterAsync(holdsCapability: false);
        (await no.Client.GetAsync("/api/tenants/members/invite", Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
