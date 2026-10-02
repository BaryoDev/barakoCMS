using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Models;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// The delivery API's OpenAPI document, asserted against the document the running app serves.
/// </summary>
/// <remarks>
/// A content type is created by a user at runtime, so its paths cannot come from anything built at
/// compile time. These assert the two halves that matter: the paths do appear without a restart,
/// and a schema is disclosure, so nothing appears that the anonymous delivery endpoint would not
/// return. The masking assertions are the ones worth breaking the code to check: a field the
/// delivery API strips must not be named in the schema, because a field name is itself information.
/// </remarks>
[Collection("Sequential")]
public class DeliveryOpenApiTests
{
    private readonly IntegrationTestFixture _factory;
    private readonly HttpClient _client;

    public DeliveryOpenApiTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private static string TypeName(string prefix) => prefix + Guid.NewGuid().ToString("n")[..8];

    private async Task<JsonDocument> DocumentAsync()
        => await OpenApiTagTests.FetchDocumentAsync(_factory);

    private static JsonElement Paths(JsonDocument doc) => doc.RootElement.GetProperty("paths");

    private static JsonElement Schemas(JsonDocument doc)
        => doc.RootElement.GetProperty("components").GetProperty("schemas");

    private static bool HasPath(JsonDocument doc, string path) => Paths(doc).TryGetProperty(path, out _);

    /// <summary>Creates a content type through the API, which is also what invalidates the cache.</summary>
    private async Task CreateTypeAsync(string name, bool deliverable, params object[] fields)
    {
        await AuthenticateAsync();
        var response = await _client.PostAsJsonAsync("/api/content-types", new
        {
            name,
            displayName = name,
            isPubliclyDeliverable = deliverable,
            fields,
        });

        response.IsSuccessStatusCode.Should().BeTrue(
            "creating the type is setup, not the assertion (got {0}: {1})",
            response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private async Task AuthenticateAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var role = await session.Query<Role>().FirstOrDefaultAsync(r => r.Name == "SuperAdmin");
        if (role is null)
        {
            role = new Role { Id = Guid.NewGuid(), Name = "SuperAdmin" };
            session.Store(role);
        }

        var userId = Guid.NewGuid();
        session.Store(new User
        {
            Id = userId,
            Username = $"openapi-{userId:n}",
            Email = $"openapi-{userId:n}@example.com",
            RoleIds = [role.Id],
        });
        await session.SaveChangesAsync();

        _client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(roles: ["Admin", "SuperAdmin"], userId: userId.ToString()));
    }

    [Fact]
    public async Task A_deliverable_type_gets_its_three_paths_with_no_restart()
    {
        var type = TypeName("students");
        await CreateTypeAsync(type, deliverable: true,
            new { name = "Title", type = "string" },
            new { name = "Slug", type = "slug" });

        using var doc = await DocumentAsync();

        HasPath(doc, $"/api/public/{type}").Should().BeTrue();
        HasPath(doc, $"/api/public/{type}/search").Should().BeTrue();
        HasPath(doc, $"/api/public/{type}/{{slug}}").Should().BeTrue();
    }

    [Fact]
    public async Task The_generic_type_route_is_still_documented()
    {
        using var doc = await DocumentAsync();

        // The delivery paths are added to the document, not swapped in for the route that actually
        // matches. Losing /api/public/{type} would break every existing consumer's client.
        HasPath(doc, "/api/public/{type}").Should().BeTrue();
    }

    [Fact]
    public async Task A_type_that_is_not_publicly_deliverable_appears_nowhere_including_its_name()
    {
        var type = TypeName("ledger");
        await CreateTypeAsync(type, deliverable: false,
            new { name = "Amount", type = "decimal" });

        using var doc = await DocumentAsync();

        // Not "has no paths": the whole document must not contain the name, because naming a type
        // nobody can fetch confirms it exists.
        doc.RootElement.GetRawText().Should().NotContain(type,
            "a type that is not publicly deliverable must not be named in the document at all");
    }

    [Fact]
    public async Task A_field_the_delivery_api_masks_is_absent_from_the_schema()
    {
        var type = TypeName("members");
        await CreateTypeAsync(type, deliverable: true,
            new { name = "Title", type = "string" },
            new { name = "GuardianContactNumber", type = "string", sensitivity = "Sensitive" },
            new { name = "InternalNote", type = "string", sensitivity = "Hidden" });

        using var doc = await DocumentAsync();

        var fields = Schemas(doc)
            .GetProperty($"Public{char.ToUpperInvariant(type[0])}{type[1..]}Fields")
            .GetProperty("properties");

        fields.TryGetProperty("Title", out _).Should().BeTrue("a Public field is what the API returns");
        fields.TryGetProperty("GuardianContactNumber", out _).Should().BeFalse(
            "a Sensitive field is stripped from the payload, and its name alone tells a reader what to probe for");
        fields.TryGetProperty("InternalNote", out _).Should().BeFalse(
            "a Hidden field is stripped from the payload");

        // Belt and braces: the names must not survive anywhere else in the document either, such as
        // in an example or a description.
        doc.RootElement.GetRawText().Should().NotContain("GuardianContactNumber");
        doc.RootElement.GetRawText().Should().NotContain("InternalNote");
    }

    [Fact]
    public async Task Validation_rules_and_defaults_are_never_published()
    {
        var type = TypeName("applicants");
        await CreateTypeAsync(type, deliverable: true, new
        {
            name = "ReferenceCode",
            type = "string",
            defaultValue = "SEEDED-INTERNAL-VALUE",
            validationRules = new Dictionary<string, object> { ["regex"] = "^ACME-[0-9]{6}$" },
        });

        using var doc = await DocumentAsync();
        var raw = doc.RootElement.GetRawText();

        raw.Should().NotContain("SEEDED-INTERNAL-VALUE",
            "a default value can carry seeded or internal data");
        raw.Should().NotContain("^ACME-",
            "a regex encodes a business rule or an upstream system's key shape, and this API accepts no input");
    }

    [Fact]
    public async Task A_type_with_no_slug_field_gets_no_slug_path()
    {
        var type = TypeName("notices");
        await CreateTypeAsync(type, deliverable: true, new { name = "Title", type = "string" });

        using var doc = await DocumentAsync();

        HasPath(doc, $"/api/public/{type}").Should().BeTrue();
        HasPath(doc, $"/api/public/{type}/{{slug}}").Should().BeFalse(
            "the slug endpoint 404s for a type with no slug field, and a path that always 404s is worse than a missing one");
    }

    [Fact]
    public async Task The_document_is_cached_and_a_direct_write_does_not_show_until_invalidated()
    {
        // Warm the cache.
        using (var _ = await DocumentAsync()) { }

        // Stored straight into Marten, so the endpoint that invalidates the cache never runs.
        var type = TypeName("smuggled");
        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new ContentTypeDefinition
            {
                Id = Guid.NewGuid(),
                Name = type,
                DisplayName = type,
                IsPubliclyDeliverable = true,
                Fields = [new FieldDefinition { Name = "Title", Type = "string" }],
            });
            await session.SaveChangesAsync();
        }

        using (var cached = await DocumentAsync())
        {
            HasPath(cached, $"/api/public/{type}").Should().BeFalse(
                "the document is served from cache, which is what stops every Swagger request hitting the database");
        }

        _factory.Services.GetRequiredService<barakoCMS.Infrastructure.OpenApi.DeliveryDocumentCache>()
            .Invalidate(Tenant.DefaultSlug);

        using var fresh = await DocumentAsync();
        HasPath(fresh, $"/api/public/{type}").Should().BeTrue("invalidating rebuilds it");
    }

    [Fact]
    public async Task Turning_delivery_off_removes_the_paths()
    {
        var type = TypeName("archive");
        await CreateTypeAsync(type, deliverable: true, new { name = "Title", type = "string" });

        using (var on = await DocumentAsync())
        {
            HasPath(on, $"/api/public/{type}").Should().BeTrue();
        }

        var response = await _client.PutAsJsonAsync($"/api/content-types/{type}/public-delivery", new { enabled = false });
        response.IsSuccessStatusCode.Should().BeTrue("turning delivery off is setup (got {0})", response.StatusCode);

        using var off = await DocumentAsync();
        off.RootElement.GetRawText().Should().NotContain(type,
            "a type withdrawn from delivery must leave the document, immediately, with no restart");
    }

    [Fact]
    public void The_schema_name_is_an_identifier_a_generator_can_use()
    {
        var name = barakoCMS.Infrastructure.OpenApi.DeliveryDocument.SchemaName("blog-posts 2024");
        name.Should().Be("BlogPosts2024");
        barakoCMS.Infrastructure.OpenApi.DeliveryDocument.SchemaName("2024-intake").Should().Be("Type2024Intake");
    }
}
