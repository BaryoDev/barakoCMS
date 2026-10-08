using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.ContentApi;

/// <summary>
/// A create is checked against the entry as it will be stored, so the conditions of a Create rule
/// hold for it the way an Update rule's hold for an edit.
/// </summary>
[Collection("Sequential")]
public class ContentCreateRuleTests
{
    private readonly IntegrationTestFixture _factory;

    public ContentCreateRuleTests(IntegrationTestFixture factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PermissionRule BranchA() => new()
    {
        Enabled = true,
        Conditions = new Dictionary<string, object>
        {
            ["Branch"] = new Dictionary<string, object> { ["_eq"] = "A" },
        },
    };

    private async Task<string> StoreTypeAsync(bool withSlug = false)
    {
        var type = $"ccr{Guid.NewGuid():N}"[..16];
        var fields = new List<FieldDefinition>
        {
            new() { Name = "Title", DisplayName = "Title", Type = "string" },
            new() { Name = "Branch", DisplayName = "Branch", Type = "string" },
        };
        if (withSlug)
            fields.Add(new FieldDefinition { Name = "slug", DisplayName = "Slug", Type = "slug", IsRequired = true });

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new ContentTypeDefinition { Id = Guid.NewGuid(), Name = type, DisplayName = type, Fields = fields });
        await session.SaveChangesAsync(Ct);
        return type;
    }

    /// <summary>A stored user holding one stored role that may read the type and create under this rule.</summary>
    private async Task<HttpClient> MemberAsync(string type, PermissionRule create)
    {
        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = $"Creator {Guid.NewGuid():N}",
            Permissions =
            [
                new ContentTypePermission
                {
                    ContentTypeSlug = type,
                    Read = new PermissionRule { Enabled = true },
                    Create = create,
                },
            ],
        };
        var userId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(role);
            session.Store(new User
            {
                Id = userId,
                Username = $"ccr-{userId:n}",
                Email = $"ccr-{userId:n}@example.com",
                RoleIds = [role.Id],
            });
            await session.SaveChangesAsync(Ct);
        }

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken([role.Name], userId.ToString()));
        return client;
    }

    private async Task<List<Content>> EntriesAsync(string type)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return [.. await session.Query<Content>().Where(c => c.ContentType == type).ToListAsync(Ct)];
    }

    private static string? Branch(Content entry) =>
        entry.Data.TryGetValue("Branch", out var value) ? value?.ToString() : null;

    [Fact]
    public async Task A_member_whose_create_rule_is_limited_to_branch_A_cannot_create_in_branch_B()
    {
        var type = await StoreTypeAsync();
        var member = await MemberAsync(type, BranchA());

        var outside = await member.PostAsJsonAsync("/api/contents",
            new { contentType = type, data = new Dictionary<string, object> { ["Title"] = "b", ["Branch"] = "B" } }, Ct);
        outside.StatusCode.Should().Be(HttpStatusCode.Forbidden, await outside.Content.ReadAsStringAsync(Ct));

        var inside = await member.PostAsJsonAsync("/api/contents",
            new { contentType = type, data = new Dictionary<string, object> { ["Title"] = "a", ["Branch"] = "A" } }, Ct);
        inside.StatusCode.Should().Be(HttpStatusCode.OK, await inside.Content.ReadAsStringAsync(Ct));

        var entries = await EntriesAsync(type);
        entries.Should().HaveCount(1, "the refused create stored nothing");
        Branch(entries[0]).Should().Be("A");
    }

    [Fact]
    public async Task A_create_rule_on_the_creator_still_lets_the_caller_create()
    {
        var type = await StoreTypeAsync();
        var member = await MemberAsync(type, new PermissionRule
        {
            Enabled = true,
            Conditions = new Dictionary<string, object>
            {
                ["$createdBy"] = new Dictionary<string, object> { ["_eq"] = "$CURRENT_USER" },
            },
        });

        var res = await member.PostAsJsonAsync("/api/contents",
            new { contentType = type, data = new Dictionary<string, object> { ["Title"] = "mine", ["Branch"] = "B" } }, Ct);

        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));
        (await EntriesAsync(type)).Should().HaveCount(1);
    }

    [Fact]
    public async Task A_push_cannot_create_an_entry_outside_the_branch_the_create_rule_is_limited_to()
    {
        var type = await StoreTypeAsync(withSlug: true);
        var member = await MemberAsync(type, BranchA());

        var outside = await member.PostAsJsonAsync($"/api/collections/{type}/push", new
        {
            entries = new[] { new Dictionary<string, object> { ["slug"] = "b", ["Title"] = "b", ["Branch"] = "B" } },
        }, Ct);
        outside.StatusCode.Should().Be(HttpStatusCode.Forbidden, await outside.Content.ReadAsStringAsync(Ct));
        (await EntriesAsync(type)).Should().BeEmpty("a refused push writes nothing");

        var inside = await member.PostAsJsonAsync($"/api/collections/{type}/push", new
        {
            entries = new[] { new Dictionary<string, object> { ["slug"] = "a", ["Title"] = "a", ["Branch"] = "A" } },
        }, Ct);
        inside.StatusCode.Should().Be(HttpStatusCode.OK, await inside.Content.ReadAsStringAsync(Ct));

        var entries = await EntriesAsync(type);
        entries.Should().HaveCount(1);
        Branch(entries[0]).Should().Be("A");
    }

    [Fact]
    public async Task An_import_row_outside_the_branch_the_create_rule_is_limited_to_is_refused()
    {
        var type = await StoreTypeAsync();
        var member = await MemberAsync(type, BranchA());

        var res = await member.PostAsJsonAsync("/api/import/content", new
        {
            contentType = type,
            continueOnError = true,
            records = new[]
            {
                new Dictionary<string, object> { ["Title"] = "a", ["Branch"] = "A" },
                new Dictionary<string, object> { ["Title"] = "b", ["Branch"] = "B" },
            },
        }, Ct);

        var body = await res.Content.ReadAsStringAsync(Ct);
        res.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", res.StatusCode, body);
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("created").GetInt32().Should().Be(1);
        var errors = doc.RootElement.GetProperty("errors").EnumerateArray().ToList();
        errors.Should().ContainSingle();
        errors[0].GetProperty("row").GetInt32().Should().Be(1);

        var entries = await EntriesAsync(type);
        entries.Should().HaveCount(1);
        Branch(entries[0]).Should().Be("A");
    }
}
