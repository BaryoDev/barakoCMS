using System.Net;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Models;
using Xunit;

namespace BarakoCMS.Tests.Features.Public;

/// <summary>
/// <c>GET /api/public/types/{type}/description</c> tells an anonymous renderer a deliverable type's
/// route template and the role of each Public field, and names nothing delivery would not return
/// (#1108). It takes no route a content type is read on, whatever the type is called.
/// </summary>
[Collection("Sequential")]
public class PublicTypeDescriptionTests
{
    private readonly IntegrationTestFixture _factory;
    private readonly HttpClient _anon;

    public PublicTypeDescriptionTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _anon = factory.CreateClient();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Route(string type) => $"/api/public/types/{type}/description";

    private async Task<string> SeedTypeAsync(bool deliverable, string? routeTemplate, params FieldDefinition[] fields)
    {
        var type = $"ptd{Guid.NewGuid():N}"[..16];
        await using var session = _factory.Services.GetRequiredService<IDocumentStore>().LightweightSession();
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = type,
            DisplayName = "Event",
            IsPubliclyDeliverable = deliverable,
            RouteTemplate = routeTemplate,
            Fields = fields.ToList(),
        });
        await session.SaveChangesAsync(Ct);
        return type;
    }

    private static FieldDefinition[] EventFields() =>
    [
        new() { Name = "Title", DisplayName = "Title", Type = "string", Role = "title" },
        new() { Name = "Body", DisplayName = "Body", Type = "markdown", Role = "summary", Editor = null },
        new() { Name = "StartsAt", DisplayName = "Starts", Type = "datetime", Role = "date" },
        new() { Name = "Cover", DisplayName = "Cover", Type = "url", Editor = "image" },
        new() { Name = "OrganiserPhone", DisplayName = "Phone", Type = "string", Sensitivity = SensitivityLevel.Sensitive },
        new() { Name = "DoorCode", DisplayName = "Door code", Type = "string", Sensitivity = SensitivityLevel.Hidden },
    ];

    private async Task<JsonElement> DescribeAsync(string type)
    {
        var response = await _anon.GetAsync(Route(type), Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var parsed = JsonDocument.Parse(body);
        return parsed.RootElement.Clone();
    }

    [Fact]
    public async Task A_public_type_answers_its_route_template_and_each_public_fields_role_and_editor()
    {
        var type = await SeedTypeAsync(deliverable: true, "/whats-on/{slug}", EventFields());

        var described = await DescribeAsync(type);

        described.GetProperty("name").GetString().Should().Be(type);
        described.GetProperty("routeTemplate").GetString().Should().Be("/whats-on/{slug}");

        var fields = described.GetProperty("fields").EnumerateArray().ToList();
        fields.Should().HaveCount(4, "the four Public fields, in declared order");
        fields.Select(f => f.GetProperty("name").GetString())
            .Should().Equal("Title", "Body", "StartsAt", "Cover");
        fields.Select(f => f.GetProperty("role").GetString())
            .Should().Equal("title", "summary", "date", null);
        fields.Select(f => f.GetProperty("type").GetString())
            .Should().Equal("string", "markdown", "datetime", "url");
        fields[3].GetProperty("editor").GetString().Should().Be("image");
    }

    [Fact]
    public async Task A_Sensitive_or_Hidden_field_is_not_named_and_no_field_reports_its_sensitivity()
    {
        var type = await SeedTypeAsync(deliverable: true, null, EventFields());

        var response = await _anon.GetAsync(Route(type), Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        body.Should().Contain("Title", "the control: a Public field is listed");
        body.Should().NotContain("OrganiserPhone");
        body.Should().NotContain("DoorCode");
        body.Should().NotContain("sensitivity", "only name, type, role and editor are described");
    }

    [Fact]
    public async Task A_token_field_is_not_listed_even_when_stored_Public()
    {
        var type = await SeedTypeAsync(deliverable: true, null,
            new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string" },
            new FieldDefinition { Name = "Ticket", DisplayName = "Ticket", Type = barakoCMS.Core.Validation.TokenFields.TypeName });

        var fields = (await DescribeAsync(type)).GetProperty("fields").EnumerateArray().ToList();

        fields.Should().HaveCount(1, "delivery never returns a token's value");
        fields[0].GetProperty("name").GetString().Should().Be("Title");
    }

    [Fact]
    public async Task A_type_that_is_not_publicly_deliverable_answers_404_like_an_unknown_one()
    {
        var hidden = await SeedTypeAsync(deliverable: false, "/staff/{slug}", EventFields());

        var refused = await _anon.GetAsync(Route(hidden), Ct);
        var unknown = await _anon.GetAsync(Route($"nosuch{Guid.NewGuid():N}"), Ct);

        refused.StatusCode.Should().Be(HttpStatusCode.NotFound);
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await refused.Content.ReadAsStringAsync(Ct)).Should().NotContain("staff");
    }

    [Fact]
    public async Task A_type_without_a_valid_route_template_answers_null_for_it()
    {
        var none = await SeedTypeAsync(deliverable: true, null, EventFields());
        var invalid = await SeedTypeAsync(deliverable: true, "//other.example/{slug}", EventFields());

        (await DescribeAsync(none)).GetProperty("routeTemplate").ValueKind.Should().Be(JsonValueKind.Null);
        (await DescribeAsync(invalid)).GetProperty("routeTemplate").ValueKind.Should().Be(JsonValueKind.Null,
            "a stored template a save would refuse is passed over, as the feed and the sitemap pass it over");
    }

    [Fact]
    public async Task The_answer_is_cacheable_and_varies_by_tenant()
    {
        var type = await SeedTypeAsync(deliverable: true, null, EventFields());

        var response = await _anon.GetAsync(Route(type), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.Public.Should().BeTrue();
        response.Headers.CacheControl.MaxAge.Should().Be(TimeSpan.FromSeconds(60));
        response.Headers.Vary.Should().Contain("X-Tenant");
    }

    /// <summary>
    /// A type named <c>types</c> holding an entry whose slug is another type's name: the slug read
    /// returns the entry, and the description sits one segment deeper.
    /// </summary>
    [Fact]
    public async Task A_type_named_types_is_still_read_by_slug_where_its_slug_names_another_type()
    {
        var tenant = $"ptd-{Guid.NewGuid():N}"[..14];
        var other = $"blog{Guid.NewGuid():N}"[..12];
        using (var scope = _factory.Services.CreateScope())
        {
            var registry = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            registry.Store(new Tenant { Id = Guid.NewGuid(), Slug = tenant, Name = tenant, IsActive = true });
            await registry.SaveChangesAsync(Ct);
        }

        await using (var session = _factory.Services.GetRequiredService<IDocumentStore>().LightweightSession(tenant))
        {
            FieldDefinition[] fields =
            [
                new() { Name = "Title", DisplayName = "Title", Type = "string", Role = "title" },
                new() { Name = "Slug", DisplayName = "Slug", Type = "slug" },
            ];
            session.Store(new ContentTypeDefinition
            {
                Id = Guid.NewGuid(), Name = "types", DisplayName = "Types", IsPubliclyDeliverable = true, Fields = fields.ToList(),
            });
            session.Store(new ContentTypeDefinition
            {
                Id = Guid.NewGuid(), Name = other, DisplayName = "Blog", IsPubliclyDeliverable = true, Fields = fields.ToList(),
            });
            session.Store(new Content
            {
                Id = Guid.NewGuid(),
                ContentType = "types",
                Status = ContentStatus.Published,
                Sensitivity = SensitivityLevel.Public,
                Data = new Dictionary<string, object> { ["Title"] = "An entry of types", ["Slug"] = other },
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await session.SaveChangesAsync(Ct);
        }

        async Task<JsonElement> GetAsync(string url)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("X-Tenant", tenant);
            var response = await _anon.SendAsync(request, Ct);
            var body = await response.Content.ReadAsStringAsync(Ct);
            response.StatusCode.Should().Be(HttpStatusCode.OK, body);
            using var parsed = JsonDocument.Parse(body);
            return parsed.RootElement.Clone();
        }

        var entry = await GetAsync($"/api/public/types/{other}");
        entry.GetProperty("contentType").GetString().Should().Be("types",
            "the slug route still answers for a type named types");
        entry.GetProperty("slug").GetString().Should().Be(other);

        var described = await GetAsync(Route(other));
        described.GetProperty("name").GetString().Should().Be(other, "the description is one segment deeper");
    }

    [Fact]
    public async Task The_OpenAPI_document_lists_the_route()
    {
        using var doc = await OpenApiTagTests.FetchDocumentAsync(_factory);

        doc.RootElement.GetProperty("paths").TryGetProperty("/api/public/types/{type}/description", out _)
            .Should().BeTrue();
    }
}
