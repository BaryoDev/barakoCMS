using System.Net;
using System.Text.Json;
using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// A read rule of <c>$branch eq $CURRENT_BRANCH</c> shows the rows of the request's branch, and only
/// of a branch the member holds.
/// </summary>
[Collection("Sequential")]
public class BranchRowScopeTests
{
    private readonly IntegrationTestFixture _factory;
    private readonly WebApplicationFactory<Program> _host;

    public BranchRowScopeTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _host = factory.WithSettings(new Dictionary<string, string?> { ["Barako:NodeId"] = Guid.NewGuid().ToString() });
    }

    private static PermissionRule SameBranch() => new()
    {
        Enabled = true,
        Conditions = new()
        {
            ["$branch"] = new Dictionary<string, object> { ["_eq"] = "$CURRENT_BRANCH" },
        },
    };

    /// <summary>A member of the default tenant whose one role reads the type under the branch rule.</summary>
    private Task<User> MemberAsync(string type, params Guid[] branches)
        => MemberAsync(new ContentTypePermission { ContentTypeSlug = type, Read = SameBranch() }, branches);

    /// <summary>A member of the default tenant whose one role holds the given permission.</summary>
    private async Task<User> MemberAsync(ContentTypePermission permission, params Guid[] branches)
    {
        using var scope = _host.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = $"Ward_{Guid.NewGuid():n}",
            Permissions = [permission],
        };
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = $"ward_{Guid.NewGuid():n}",
            Email = $"ward_{Guid.NewGuid():n}@example.com",
            RoleIds = [role.Id],
        };
        session.Store(role);
        session.Store(user);
        session.Store(new Membership
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TenantSlug = Tenant.DefaultSlug,
            Status = MembershipStatus.Active,
            RoleIds = [role.Id],
            Profile = new Dictionary<string, string> { ["branches"] = string.Join(",", branches) },
        });
        await session.SaveChangesAsync();
        return user;
    }

    /// <summary>One entry of the type in each branch given, stored directly.</summary>
    private async Task<Dictionary<Guid, Guid>> EntriesAsync(string type, params Guid[] branches)
    {
        using var scope = _host.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var byBranch = new Dictionary<Guid, Guid>();

        foreach (var branch in branches)
        {
            var content = new Content
            {
                Id = Guid.NewGuid(),
                ContentType = type,
                Data = new() { ["Title"] = $"ward in {branch}" },
                BranchId = branch,
                Status = ContentStatus.Draft,
            };
            session.Store(content);
            byBranch[branch] = content.Id;
        }

        await session.SaveChangesAsync();
        return byBranch;
    }

    private async Task<(HttpStatusCode Status, List<Guid> Ids)> ListAsync(User user, string type, Guid? branch)
    {
        var client = _host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(roles: [], userId: user.Id.ToString()));

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/contents?contentType={type}&pageSize=50");
        if (branch is not null) request.Headers.Add("X-Branch-Id", branch.ToString());

        var res = await client.SendAsync(request);
        if (res.StatusCode != HttpStatusCode.OK) return (res.StatusCode, []);

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return (res.StatusCode, doc.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("id").GetGuid()).ToList());
    }

    [Fact]
    public async Task A_user_with_two_branches_sees_only_the_request_branch()
    {
        var type = $"ward{Guid.NewGuid():n}"[..20];
        Guid b1 = Guid.NewGuid(), b2 = Guid.NewGuid(), b3 = Guid.NewGuid();
        var user = await MemberAsync(type, b1, b2);
        var entries = await EntriesAsync(type, b1, b2);

        (await ListAsync(user, type, b1)).Should().Be((HttpStatusCode.OK, new List<Guid> { entries[b1] }));
        (await ListAsync(user, type, b2)).Ids.Should().Equal(entries[b2]);

        var none = await ListAsync(user, type, branch: null);
        none.Status.Should().Be(HttpStatusCode.OK);
        none.Ids.Should().BeEmpty("with no X-Branch-Id there is no current branch to match");

        (await ListAsync(user, type, b3)).Status.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_resolver_ignores_a_branch_that_is_not_in_the_list()
    {
        var type = $"ward{Guid.NewGuid():n}"[..20];
        Guid b1 = Guid.NewGuid(), b2 = Guid.NewGuid(), b3 = Guid.NewGuid();
        var user = await MemberAsync(type, b1, b2);

        async Task<bool> CanReadAsync(Guid requestBranch, Guid rowBranch)
        {
            using var scope = _host.Services.CreateScope();
            using var _ = scope.ServiceProvider.GetRequiredService<IEventEnvelopeContext>().Use(requestBranch);
            var resolver = scope.ServiceProvider.GetRequiredService<PermissionResolver>();
            var row = new Content { Id = Guid.NewGuid(), ContentType = type, BranchId = rowBranch };
            return await resolver.CanPerformActionAsync(user, type, "read", row);
        }

        (await CanReadAsync(b1, b1)).Should().BeTrue("the rule grants a held branch, so the denial below means something");
        (await CanReadAsync(b3, b3)).Should().BeFalse("B3 is not one of the member's branches");

        using var scope = _host.Services.CreateScope();
        using var held = scope.ServiceProvider.GetRequiredService<IEventEnvelopeContext>().Use(b3);
        var predicate = await scope.ServiceProvider.GetRequiredService<PermissionResolver>().ReadPredicateAsync(user, type);
        predicate.Sql.Should().Be("(FALSE)");
    }

    [Fact]
    public async Task An_update_under_a_branch_rule_keeps_the_stored_branch()
    {
        var type = $"ward{Guid.NewGuid():n}"[..20];
        Guid b1 = Guid.NewGuid(), b2 = Guid.NewGuid();
        var user = await MemberAsync(new ContentTypePermission { ContentTypeSlug = type, Update = SameBranch() }, b1, b2);

        async Task<bool> CanUpdateAsync(Guid requestBranch, Guid rowBranch)
        {
            using var scope = _host.Services.CreateScope();
            using var _ = scope.ServiceProvider.GetRequiredService<IEventEnvelopeContext>().Use(requestBranch);
            var resolver = scope.ServiceProvider.GetRequiredService<IPermissionResolver>();
            var row = new Content { Id = Guid.NewGuid(), ContentType = type, BranchId = rowBranch, Data = new() { ["Title"] = "old" } };
            return await resolver.AllowsWrittenEntryAsync(user, row, "update",
                new Dictionary<string, object> { ["Title"] = "new" }, CancellationToken.None);
        }

        (await CanUpdateAsync(b1, b1)).Should().BeTrue("the written copy keeps the row's branch, which matches the request");
        (await CanUpdateAsync(b1, b2)).Should().BeFalse("a row of another branch stays out of reach");
    }

    [Fact]
    public async Task A_create_under_a_branch_rule_is_judged_in_the_request_branch()
    {
        var type = $"ward{Guid.NewGuid():n}"[..20];
        Guid b1 = Guid.NewGuid(), b3 = Guid.NewGuid();
        var user = await MemberAsync(new ContentTypePermission { ContentTypeSlug = type, Create = SameBranch() }, b1);

        async Task<bool> CanCreateAsync(Guid requestBranch)
        {
            using var scope = _host.Services.CreateScope();
            using var _ = scope.ServiceProvider.GetRequiredService<IEventEnvelopeContext>().Use(requestBranch);
            var resolver = scope.ServiceProvider.GetRequiredService<IPermissionResolver>();
            var request = new ContentCreateRequest
            {
                ContentType = type,
                Data = new Dictionary<string, object> { ["Title"] = "new" },
            };
            return await resolver.AllowsCreatedEntryAsync(user, request, CancellationToken.None);
        }

        (await CanCreateAsync(b1)).Should().BeTrue("the entry will be stamped with the request branch, which the member holds");
        (await CanCreateAsync(b3)).Should().BeFalse("B3 is not one of the member's branches");
    }
}
