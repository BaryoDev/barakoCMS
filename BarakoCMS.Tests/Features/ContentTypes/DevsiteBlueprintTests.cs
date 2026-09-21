using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.ContentTypes;

/// <summary>
/// The <c>devsite</c> blueprint (issue #721): the shape barakocms.com needs, built from the core
/// blueprint mechanism rather than a new one.
/// </summary>
/// <remarks>
/// Two things the issue asserted about blueprints in general are re-checked here against this
/// specific file, rather than trusted from the issue text: a reference has to resolve inside the
/// blueprint itself, so the kit works on a tenant that has applied nothing else, and a clashing type
/// name refuses the whole apply rather than merging around it.
/// </remarks>
[Collection("Sequential")]
public class DevsiteBlueprintTests
{
    private readonly IntegrationTestFixture _factory;

    public DevsiteBlueprintTests(IntegrationTestFixture factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] ExpectedTypes =
        ["page", "post", "category", "author", "doc", "package", "release", "contributor", "up-for-grabs"];

    private async Task<string> TenantAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var slug = $"ds-{Guid.NewGuid():N}"[..14].ToLowerInvariant();
        session.Store(new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = slug, IsActive = true });
        await session.SaveChangesAsync(Ct);
        return slug;
    }

    private async Task<HttpClient> AdminInAsync(string tenantSlug)
    {
        var userId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new User
            {
                Id = userId,
                Username = $"ds-{Guid.NewGuid():n}"[..14],
                Email = $"ds-{Guid.NewGuid():n}@example.com",
                RoleIds = [SystemRoles.SuperAdminRoleId],
            });
            session.Store(new Membership
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                TenantSlug = tenantSlug,
                Status = MembershipStatus.Active,
                RoleIds = [SystemRoles.SuperAdminRoleId],
            });
            await session.SaveChangesAsync(Ct);
        }

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(
                roles: ["SuperAdmin"],
                userId: userId.ToString(),
                additionalClaims: new Dictionary<string, string> { ["tenant"] = tenantSlug }));
        client.DefaultRequestHeaders.Add("X-Tenant", tenantSlug);
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, $"10.11.{Random.Shared.Next(1, 250)}.{Random.Shared.Next(1, 250)}");
        return client;
    }

    private static Task<HttpResponseMessage> ApplyAsync(HttpClient client) =>
        client.PostAsync("/api/content-types/blueprints/devsite", null, Ct);

    private static async Task<List<string>> TypeNamesAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/content-types?pageSize=100", Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return doc.RootElement.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("name").GetString()!)
            .ToList();
    }

    private static async Task<JsonElement> TypeAsync(HttpClient client, string name)
    {
        var response = await client.GetAsync("/api/content-types?pageSize=100", Ct);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return doc.RootElement.GetProperty("items").EnumerateArray()
            .First(i => i.GetProperty("name").GetString() == name)
            .Clone();
    }

    /// <summary>
    /// Applying devsite alone, with no other blueprint applied first, has to succeed. A page-typed
    /// reference (post -&gt; author/category, page -&gt; page, doc -&gt; doc) resolving only because
    /// some other blueprint happened to run earlier would pass in every test here, since the suite
    /// applies plenty of blueprints, and fail only on the fresh tenant a real client starts from.
    /// </summary>
    [Fact]
    public async Task Applying_devsite_alone_on_a_fresh_tenant_creates_every_type_with_no_missing_reference()
    {
        var client = await AdminInAsync(await TenantAsync());

        var response = await ApplyAsync(client);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        doc.RootElement.GetProperty("blueprint").GetString().Should().Be("devsite");
        var created = doc.RootElement.GetProperty("created").EnumerateArray()
            .Select(c => c.GetProperty("name").GetString()).ToList();
        created.Should().BeEquivalentTo(ExpectedTypes);

        var names = await TypeNamesAsync(client);
        names.Should().Contain(ExpectedTypes);

        var post = await TypeAsync(client, "post");
        post.GetProperty("fields").EnumerateArray()
            .Single(f => f.GetProperty("name").GetString() == "Author")
            .GetProperty("referenceType").GetString().Should().Be("author");
        post.GetProperty("fields").EnumerateArray()
            .Single(f => f.GetProperty("name").GetString() == "Category")
            .GetProperty("referenceType").GetString().Should().Be("category");

        var page = await TypeAsync(client, "page");
        page.GetProperty("fields").EnumerateArray()
            .Single(f => f.GetProperty("name").GetString() == "ParentPage")
            .GetProperty("referenceType").GetString().Should().Be("page");

        var article = await TypeAsync(client, "doc");
        article.GetProperty("fields").EnumerateArray()
            .Single(f => f.GetProperty("name").GetString() == "ParentDoc")
            .GetProperty("referenceType").GetString().Should().Be("doc");
    }

    [Fact]
    public async Task Applying_devsite_twice_refuses_the_second_apply_with_409_naming_the_clash()
    {
        var client = await AdminInAsync(await TenantAsync());
        (await ApplyAsync(client)).StatusCode.Should().Be(HttpStatusCode.OK);

        var second = await ApplyAsync(client);

        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await second.Content.ReadAsStringAsync(Ct);
        body.Should().Contain("page").And.Contain("post", "the refusal names the types that clash");
        (await TypeNamesAsync(client)).Count(n => n == "page").Should().Be(1,
            "a refused apply must not have created a duplicate before it failed");
    }

    [Fact]
    public async Task A_type_name_devsite_would_create_blocks_the_whole_apply_even_if_it_was_made_by_hand()
    {
        var client = await AdminInAsync(await TenantAsync());
        var contributor = await client.PostAsJsonAsync("/api/content-types", new
        {
            name = "contributor",
            displayName = "Contributor",
            fields = new[] { new { name = "Handle", displayName = "Handle", type = "string" } },
        }, Ct);
        contributor.StatusCode.Should().Be(HttpStatusCode.OK);

        var applied = await ApplyAsync(client);

        applied.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var names = await TypeNamesAsync(client);
        names.Should().Contain("contributor");
        names.Should().NotContain("page", "a partial apply would leave post/page referencing types the blueprint never created");
        names.Should().NotContain("post");
    }
}
