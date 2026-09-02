using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests;

/// <summary>
/// A content item can point at another one, and resolving that pointer cannot see more than the
/// list already could.
/// </summary>
/// <remarks>
/// References are application level, Contentful and Sanity style, rather than real foreign keys.
/// That was the decision #141 had to make and it is forced by the rest of the design: typed columns
/// and foreign keys would mean a migration for every field added, and the content model here is
/// defined at runtime by whoever is clicking around the admin. The two cannot both be true.
///
/// So integrity is checked on write, and the read path degrades rather than failing.
/// </remarks>
[Collection("Sequential")]
public class ContentReferenceTests
{
    private readonly IntegrationTestFixture _factory;

    public ContentReferenceTests(IntegrationTestFixture factory) => _factory = factory;

    private async Task<(string Author, string Post)> SeedTypesAsync(string suffix)
    {
        var author = $"ref_author_{suffix}";
        var post = $"ref_post_{suffix}";

        using var scope = _factory.Services.CreateScope();
        var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        s.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(), Name = author, DisplayName = author, IsPubliclyDeliverable = true,
            Fields =
            [
                new FieldDefinition { Name = "Name", DisplayName = "Name", Type = "string" },
                new FieldDefinition
                {
                    Name = "Salary", DisplayName = "Salary", Type = "decimal",
                    Sensitivity = SensitivityLevel.Sensitive,
                },
            ],
        });

        s.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(), Name = post, DisplayName = post, IsPubliclyDeliverable = true,
            Fields =
            [
                new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string" },
                new FieldDefinition
                {
                    Name = "Author", DisplayName = "Author", Type = "reference", ReferenceType = author,
                },
            ],
        });

        await s.SaveChangesAsync();
        return (author, post);
    }

    private Guid StoreContent(IDocumentSession s, string type, Dictionary<string, object> data,
        ContentStatus status = ContentStatus.Published)
    {
        var id = Guid.NewGuid();
        s.Store(new Content
        {
            Id = id, ContentType = type, Status = status,
            Sensitivity = SensitivityLevel.Public, Data = data,
        });
        return id;
    }

    private async Task<HttpClient> AdminAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        // Both roles. SuperAdmin is what PermissionResolver bypasses on, and it reads the stored
        // user rather than the token. Admin is what the content-type endpoints gate on at the
        // FastEndpoints layer, which reads the token. The two checks are in different places and
        // satisfying one does not satisfy the other.
        var roleIds = new List<Guid>();
        foreach (var name in new[] { "SuperAdmin", "Admin" })
        {
            var role = await s.Query<Role>().FirstOrDefaultAsync(r => r.Name == name);
            if (role is null) { role = new Role { Id = Guid.NewGuid(), Name = name }; s.Store(role); }
            roleIds.Add(role.Id);
        }

        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = $"refadmin_{Guid.NewGuid():n}",
            Email = $"refadmin_{Guid.NewGuid():n}@example.com",
            RoleIds = roleIds,
        };
        s.Store(user);
        await s.SaveChangesAsync();

        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(roles: ["SuperAdmin", "Admin"], userId: user.Id.ToString()));
        return c;
    }

    // ---- definition time -------------------------------------------------------------------

    [Fact]
    public async Task A_reference_field_must_name_the_type_it_points_at()
    {
        var client = await AdminAsync();

        var res = await client.PostAsJsonAsync("/api/content-types", new
        {
            name = $"ref_untyped_{Guid.NewGuid():n}"[..20],
            displayName = "Untyped",
            fields = new[] { new { name = "Author", type = "reference" } },
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "a reference with no target is an untyped uuid, which is the thing this field type exists to replace");
        (await res.Content.ReadAsStringAsync()).Should().Contain("referenceType");
    }

    // ---- write time ------------------------------------------------------------------------

    [Fact]
    public async Task A_reference_to_something_that_does_not_exist_is_refused()
    {
        var (_, post) = await SeedTypesAsync(Guid.NewGuid().ToString("n")[..8]);
        var client = await AdminAsync();

        var res = await client.PostAsJsonAsync("/api/contents", new
        {
            contentType = post,
            data = new Dictionary<string, object>
            {
                ["Title"] = "orphan", ["Author"] = Guid.NewGuid().ToString(),
            },
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "a reference that points at nothing would be stored happily and fail for whoever renders it");
    }

    /// <summary>
    /// Pointing at a real entry of the wrong type is refused.
    /// </summary>
    /// <remarks>
    /// The more interesting of the two failures. It looks correct in the data bag, passes any shape
    /// check, and resolves to something the consumer never asked for.
    /// </remarks>
    [Fact]
    public async Task A_reference_to_the_wrong_type_is_refused()
    {
        var suffix = Guid.NewGuid().ToString("n")[..8];
        var (author, post) = await SeedTypesAsync(suffix);
        var client = await AdminAsync();

        Guid otherPost;
        using (var scope = _factory.Services.CreateScope())
        {
            var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            otherPost = StoreContent(s, post, new() { ["Title"] = "not an author" });
            await s.SaveChangesAsync();
        }

        var res = await client.PostAsJsonAsync("/api/contents", new
        {
            contentType = post,
            data = new Dictionary<string, object> { ["Title"] = "wrong", ["Author"] = otherPost.ToString() },
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync()).Should().Contain(author,
            "the error names the type the field is declared to point at, because not-found and "
          + "wrong-type are different mistakes");
    }

    /// <summary>The control. Without it a validator that refused every reference would pass.</summary>
    [Fact]
    public async Task A_reference_to_the_right_type_is_accepted()
    {
        var suffix = Guid.NewGuid().ToString("n")[..8];
        var (author, post) = await SeedTypesAsync(suffix);
        var client = await AdminAsync();

        Guid authorId;
        using (var scope = _factory.Services.CreateScope())
        {
            var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            authorId = StoreContent(s, author, new() { ["Name"] = "Ada" });
            await s.SaveChangesAsync();
        }

        var res = await client.PostAsJsonAsync("/api/contents", new
        {
            contentType = post,
            data = new Dictionary<string, object> { ["Title"] = "ok", ["Author"] = authorId.ToString() },
        });

        res.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}",
            res.StatusCode, await res.Content.ReadAsStringAsync());
    }

    // ---- read time -------------------------------------------------------------------------

    [Fact]
    public async Task Include_resolves_the_reference_in_one_request()
    {
        var suffix = Guid.NewGuid().ToString("n")[..8];
        var (author, post) = await SeedTypesAsync(suffix);

        using (var scope = _factory.Services.CreateScope())
        {
            var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var authorId = StoreContent(s, author, new() { ["Name"] = "Ada", ["Salary"] = 1m });
            StoreContent(s, post, new() { ["Title"] = "resolved", ["Author"] = authorId.ToString() });
            await s.SaveChangesAsync();
        }

        var anon = _factory.CreateClient();
        var body = await anon.GetStringAsync($"/api/public/{post}?include=Author");

        using var doc = JsonDocument.Parse(body);
        var item = doc.RootElement.GetProperty("items")[0];
        var resolved = item.GetProperty("data").GetProperty("Author");

        resolved.ValueKind.Should().Be(JsonValueKind.Object,
            "the point of include is that the pointer is replaced by the thing it points at");
        resolved.GetProperty("data").GetProperty("Name").GetString().Should().Be("Ada");
    }

    /// <summary>
    /// Resolving does not unmask a field the target's own schema hides.
    /// </summary>
    /// <remarks>
    /// The security question this whole feature turns on. A resolved entry goes through the same
    /// ToPublic projection the list uses, so the field allowlist applies to it. Reimplementing those
    /// checks in the resolver would be the obvious way to get this wrong, and it would not be
    /// visible in any test that only checked the resolved value came back.
    /// </remarks>
    [Fact]
    public async Task Resolving_does_not_expose_a_sensitive_field_on_the_target()
    {
        var suffix = Guid.NewGuid().ToString("n")[..8];
        var (author, post) = await SeedTypesAsync(suffix);

        using (var scope = _factory.Services.CreateScope())
        {
            var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var authorId = StoreContent(s, author, new() { ["Name"] = "Ada", ["Salary"] = 999999m });
            StoreContent(s, post, new() { ["Title"] = "resolved", ["Author"] = authorId.ToString() });
            await s.SaveChangesAsync();
        }

        var anon = _factory.CreateClient();
        var body = await anon.GetStringAsync($"/api/public/{post}?include=Author");

        body.Should().NotContain("999999",
            "Salary is Sensitive on the author type, and resolving a reference is not a way around that");
    }

    /// <summary>
    /// A reference to a Draft resolves to nothing, and the field is removed rather than left as an id.
    /// </summary>
    /// <remarks>
    /// This is also the answer to what a dangling reference does, which #141 asks to be defined
    /// rather than left to chance. An unresolvable target and an erased one look identical: the
    /// field is absent. Leaving the id would say "there is something here you may not see", which is
    /// the oracle the sensitivity rules exist to prevent.
    /// </remarks>
    [Fact]
    public async Task A_reference_to_an_unpublished_target_resolves_to_nothing()
    {
        var suffix = Guid.NewGuid().ToString("n")[..8];
        var (author, post) = await SeedTypesAsync(suffix);

        using (var scope = _factory.Services.CreateScope())
        {
            var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var draftAuthor = StoreContent(s, author, new() { ["Name"] = "Unpublished" }, ContentStatus.Draft);
            StoreContent(s, post, new() { ["Title"] = "points at a draft", ["Author"] = draftAuthor.ToString() });
            await s.SaveChangesAsync();
        }

        var anon = _factory.CreateClient();
        var body = await anon.GetStringAsync($"/api/public/{post}?include=Author");

        body.Should().NotContain("Unpublished", "resolving is not a second way into a Draft");

        using var doc = JsonDocument.Parse(body);
        var data = doc.RootElement.GetProperty("items")[0].GetProperty("data");
        data.TryGetProperty("Author", out _).Should().BeFalse(
            "an unreadable target is indistinguishable from no reference at all");
    }

    [Fact]
    public async Task Include_on_a_field_that_is_not_a_reference_is_refused()
    {
        var suffix = Guid.NewGuid().ToString("n")[..8];
        var (_, post) = await SeedTypesAsync(suffix);

        var anon = _factory.CreateClient();
        var res = await anon.GetAsync($"/api/public/{post}?include=Title");

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "a silently dropped include returns ids where the caller expected objects");
    }

    [Fact]
    public async Task Without_include_the_reference_is_still_its_id()
    {
        var suffix = Guid.NewGuid().ToString("n")[..8];
        var (author, post) = await SeedTypesAsync(suffix);

        Guid authorId;
        using (var scope = _factory.Services.CreateScope())
        {
            var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            authorId = StoreContent(s, author, new() { ["Name"] = "Ada" });
            StoreContent(s, post, new() { ["Title"] = "unresolved", ["Author"] = authorId.ToString() });
            await s.SaveChangesAsync();
        }

        var anon = _factory.CreateClient();
        var body = await anon.GetStringAsync($"/api/public/{post}");

        using var doc = JsonDocument.Parse(body);
        var value = doc.RootElement.GetProperty("items")[0].GetProperty("data").GetProperty("Author");

        value.ValueKind.Should().Be(JsonValueKind.String, "resolving is opt in, not the default");
        value.GetString().Should().Be(authorId.ToString());
    }
}
