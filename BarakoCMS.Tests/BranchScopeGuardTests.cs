using System.Net.Http.Json;
using System.Security.Claims;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using BarakoCMS.Tests.SampleProbe;
using FluentAssertions;
using Marten;
using Marten.Linq.MatchesSql;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static BarakoCMS.Tests.Realtime.ContentTopicKit;

namespace BarakoCMS.Tests;

/// <summary>
/// A module outside this repository resolves <see cref="IBranchScopeGuard"/> and narrows content to
/// the caller's branches with <c>FilterAsync</c> and <c>MatchesSql</c>. The callers here are
/// authenticated by the real API key handler, as a module's endpoint would see them.
/// </summary>
[Collection("Integration1")]
public class BranchScopeGuardTests(SampleProbeHost host) : IClassFixture<SampleProbeHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Created(Guid Id, string Key);

    private sealed record Setup(string Scoped, string Open, Guid A, Guid B, Guid InA, Guid InB, Guid OpenEntry, Caller Creator);

    [Fact]
    public void Branch_scope_guard_is_public()
    {
        typeof(IBranchScopeGuard).IsPublic.Should().BeTrue();
        typeof(BranchScopeFilter).IsPublic.Should().BeTrue();
        typeof(IBranchScopeGuard).Assembly.GetType("barakoCMS.Infrastructure.Services.BranchScopeGuard")!
            .IsPublic.Should().BeFalse();
        typeof(IBranchScopeGuard).Assembly.GetType("barakoCMS.Infrastructure.Envelope.IBranchMembership")!
            .IsPublic.Should().BeFalse();
    }

    private async Task<Setup> ArrangeAsync(params int[] creatorHolds)
    {
        var scoped = await TypeAsync(host, null, branchScoped: true);
        var open = await TypeAsync(host, null, branchScoped: false);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var both = await WriterAsync(host, a, b);
        var inA = await CreateAsync(both.Client, scoped, a);
        var inB = await CreateAsync(both.Client, scoped, b);
        var openEntry = await CreateAsync(both.Client, open, null);
        var creator = creatorHolds.Length == 0
            ? await WriterAsync(host)
            : await WriterAsync(host, creatorHolds.Select(i => i == 0 ? a : b).ToArray());
        return new Setup(scoped, open, a, b, inA, inB, openEntry, creator);
    }

    private async Task<string> KeyOfAsync(Caller creator, Guid? principalId = null)
    {
        var res = await creator.Client.PostAsJsonAsync("/api/api-keys",
            new { name = "ci", scopes = new[] { "content:read" }, servicePrincipalId = principalId }, Ct);
        var body = await res.Content.ReadAsStringAsync(Ct);
        res.IsSuccessStatusCode.Should().BeTrue(body);
        return (await res.Content.ReadFromJsonAsync<Created>(Ct))!.Key;
    }

    private async Task<Guid> PrincipalAsync(Caller creator)
    {
        var role = new Role { Id = Guid.NewGuid(), Name = $"bsg-{Guid.NewGuid():N}"[..20], TenantSlug = Tenant.DefaultSlug };
        await using (var session = host.OpenSession())
        {
            session.Store(role);
            await session.SaveChangesAsync(Ct);
        }

        var res = await creator.Client.PostAsJsonAsync("/api/service-principals",
            new { name = $"bsg-{Guid.NewGuid():N}"[..10], roleIds = new[] { role.Id } }, Ct);
        var body = await res.Content.ReadAsStringAsync(Ct);
        res.IsSuccessStatusCode.Should().BeTrue(body);
        return (await res.Content.ReadFromJsonAsync<Created>(Ct))!.Id;
    }

    // Authenticates the key the way the host does, then returns the ids of the entries of the given
    // types that the guard's filter lets through. Null filter is reported as a null set.
    private async Task<(ClaimsPrincipal Caller, HashSet<Guid>? Ids)> SeenAsync(string key, params string[] types)
    {
        using var scope = host.Services.CreateScope();
        var ctx = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        ctx.Request.Headers.Authorization = "Bearer " + key;
        var auth = await ctx.AuthenticateAsync(ApiKeyAuthenticationHandler.SchemeName);
        auth.Succeeded.Should().BeTrue();

        var caller = auth.Principal!;
        var filter = await scope.ServiceProvider.GetRequiredService<IBranchScopeGuard>().FilterAsync(caller, Ct);
        if (filter is null) return (caller, null);

        var rows = await scope.ServiceProvider.GetRequiredService<IQuerySession>().Query<Content>()
            .Where(c => types.Contains(c.ContentType) && c.MatchesSql(filter.Sql, filter.Parameters))
            .ToListAsync(Ct);
        return (caller, rows.Select(r => r.Id).ToHashSet());
    }

    [Fact]
    public async Task A_key_acting_as_a_user_gets_that_users_branch_rows_and_every_unscoped_row()
    {
        var s = await ArrangeAsync(0);
        var key = await KeyOfAsync(s.Creator);

        var (_, ids) = await SeenAsync(key, s.Scoped, s.Open);

        ids.Should().NotBeNull();
        ids.Should().BeEquivalentTo([s.InA, s.OpenEntry]);
        ids.Should().NotContain(s.InB);
    }

    [Fact]
    public async Task A_key_acting_as_a_principal_gets_the_principals_branches_not_the_creators()
    {
        var s = await ArrangeAsync(0, 1);
        var principal = await PrincipalAsync(s.Creator);
        var principalKey = await KeyOfAsync(s.Creator, principal);
        var creatorKey = await KeyOfAsync(s.Creator);

        var (_, creatorSees) = await SeenAsync(creatorKey, s.Scoped, s.Open);
        creatorSees.Should().BeEquivalentTo([s.InA, s.InB, s.OpenEntry]);

        // A principal has no membership and so holds no branch, however many its creator holds.
        var (_, principalSees) = await SeenAsync(principalKey, s.Scoped, s.Open);
        principalSees.Should().BeEquivalentTo([s.OpenEntry]);
    }

    [Fact]
    public async Task A_caller_holding_no_branch_gets_no_row_of_a_branch_scoped_type()
    {
        var s = await ArrangeAsync();
        var key = await KeyOfAsync(s.Creator);

        var (_, ids) = await SeenAsync(key, s.Scoped, s.Open);

        ids.Should().BeEquivalentTo([s.OpenEntry]);
    }

    [Fact]
    public async Task Two_principals_in_one_scope_get_two_answers()
    {
        var s = await ArrangeAsync(0);
        var holdsA = await KeyOfAsync(s.Creator);
        var holdsNone = await KeyOfAsync(await WriterAsync(host));

        // Each key signs in on its own scope: the auth handler caches its result per scope.
        ClaimsPrincipal first, second;
        using (var scopeOne = host.Services.CreateScope()) first = await Authenticate(scopeOne, holdsA);
        using (var scopeTwo = host.Services.CreateScope()) second = await Authenticate(scopeTwo, holdsNone);
        second.Should().NotBeSameAs(first);
        second.FindFirst("UserId")!.Value.Should().NotBe(first.FindFirst("UserId")!.Value);

        using var scope = host.Services.CreateScope();
        var guard = scope.ServiceProvider.GetRequiredService<IBranchScopeGuard>();

        var entry = new Content { ContentType = s.Scoped, BranchId = s.A };
        (await guard.MayAccessAsync(first, entry, Ct)).Should().BeTrue();
        (await guard.MayAccessAsync(second, entry, Ct)).Should().BeFalse();
        (await guard.MayAccessAsync(first, entry, Ct)).Should().BeTrue();

        var one = await guard.FilterAsync(first, Ct);
        var two = await guard.FilterAsync(second, Ct);
        var query = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var types = new[] { s.Scoped, s.Open };
        var seenByFirst = (await query.Query<Content>()
            .Where(c => types.Contains(c.ContentType) && c.MatchesSql(one!.Sql, one.Parameters))
            .ToListAsync(Ct)).Select(r => r.Id).ToList();
        var seenBySecond = (await query.Query<Content>()
            .Where(c => types.Contains(c.ContentType) && c.MatchesSql(two!.Sql, two.Parameters))
            .ToListAsync(Ct)).Select(r => r.Id).ToList();

        seenByFirst.Should().NotBeEmpty();
        seenByFirst.Should().Contain(s.InA);
        seenByFirst.Should().Contain(s.OpenEntry);
        seenBySecond.Should().BeEquivalentTo([s.OpenEntry]);
    }

    private static async Task<ClaimsPrincipal> Authenticate(IServiceScope scope, string key)
    {
        var ctx = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        ctx.Request.Headers.Authorization = "Bearer " + key;
        var auth = await ctx.AuthenticateAsync(ApiKeyAuthenticationHandler.SchemeName);
        auth.Succeeded.Should().BeTrue();
        return auth.Principal!;
    }
}
