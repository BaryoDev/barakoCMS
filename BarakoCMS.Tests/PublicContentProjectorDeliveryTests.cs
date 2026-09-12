using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using barakoCMS.Core.Interfaces;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// The projector against the running host: it resolves from the container, and what it returns
/// serialises to the same JSON <c>GET /api/public/{type}/{slug}</c> sends. That second part is the
/// promise a module is relying on, and a unit test cannot make it, since the endpoint's shape only
/// exists once it has gone through the host's serialiser.
/// </summary>
[Collection("Sequential")]
public class PublicContentProjectorDeliveryTests
{
    private readonly IntegrationTestFixture _factory;
    private readonly HttpClient _client; // anonymous, as a client site would be

    public PublicContentProjectorDeliveryTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Secret = "projector-secret-value-12345";
    private const string Slug = "projected-post";

    // A type name per test, because the tests in this collection share one database and a second copy
    // of the same type name would make the lookups below ambiguous.
    private async Task SeedAsync(string type)
    {
        using var scope = _factory.Services.CreateScope();
        var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var def = new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = type,
            DisplayName = type,
            IsPubliclyDeliverable = true,
            Fields =
            [
                new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string" },
                new FieldDefinition { Name = "Slug", DisplayName = "Slug", Type = "slug" },
                new FieldDefinition
                {
                    Name = "Secret",
                    DisplayName = "Secret",
                    Type = "string",
                    Sensitivity = SensitivityLevel.Sensitive,
                },
            ],
        };
        def.Fields.AddRange(barakoCMS.Features.Seo.SeoFields.Definitions());
        s.Store(def);

        s.Store(new Content
        {
            Id = Guid.NewGuid(),
            ContentType = type,
            Status = ContentStatus.Published,
            Sensitivity = SensitivityLevel.Public,
            Data = new()
            {
                ["Title"] = "A Projected Post",
                ["Slug"] = Slug,
                ["MetaDescription"] = "What the snippet says",
                ["Secret"] = Secret,
            },
        });

        s.Store(new Content
        {
            Id = Guid.NewGuid(),
            ContentType = type,
            Status = ContentStatus.Draft,
            Sensitivity = SensitivityLevel.Public,
            Data = new() { ["Title"] = "A Draft", ["Slug"] = "projector-draft" },
        });

        await s.SaveChangesAsync(Ct);
    }

    [Fact]
    public void The_projector_resolves_from_the_container()
    {
        using var scope = _factory.Services.CreateScope();

        scope.ServiceProvider.GetService<IPublicContentProjector>()
            .Should().NotBeNull("a module resolves it the same way, so an unregistered interface is useless");
    }

    [Fact]
    public async Task A_projected_entry_serialises_to_what_the_delivery_route_sends()
    {
        const string type = "projector_shape";
        await SeedAsync(type);

        var res = await _client.GetAsync($"/api/public/{type}/{Slug}", Ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var fromRoute = JsonNode.Parse(await res.Content.ReadAsStringAsync(Ct));
        fromRoute.Should().NotBeNull("the comparison below proves nothing against an empty body");

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var projector = scope.ServiceProvider.GetRequiredService<IPublicContentProjector>();

        var def = await session.Query<ContentTypeDefinition>().FirstOrDefaultAsync(d => d.Name == type, Ct);
        def.Should().NotBeNull();
        projector.IsDeliverable(def).Should().BeTrue();

        var entry = (await session.Query<Content>().Where(c => c.ContentType == type).ToListAsync(Ct))
            .Single(c => c.Status == ContentStatus.Published);

        var projected = projector.Project(entry, def);
        projected.Should().NotBeNull();
        projected!.Data.Should().NotBeEmpty();

        var fromProjector = JsonNode.Parse(
            JsonSerializer.Serialize(projected, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        JsonNode.DeepEquals(fromProjector, fromRoute).Should().BeTrue(
            "a module serialising the projection has to produce what the core route produces, else a "
          + "client site needs a second parser. Route: {0}. Projector: {1}",
            fromRoute!.ToJsonString(), fromProjector!.ToJsonString());

        fromProjector.ToJsonString().Should().NotContain(Secret, "the Sensitive field is gone from both");
    }

    [Fact]
    public async Task A_draft_loaded_by_hand_still_does_not_project()
    {
        const string type = "projector_draft";
        await SeedAsync(type);

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var projector = scope.ServiceProvider.GetRequiredService<IPublicContentProjector>();

        var def = await session.Query<ContentTypeDefinition>().FirstOrDefaultAsync(d => d.Name == type, Ct);
        var entries = await session.Query<Content>().Where(c => c.ContentType == type).ToListAsync(Ct);
        entries.Should().HaveCount(2, "one published and one draft were seeded");

        var draft = entries.Single(c => c.Status == ContentStatus.Draft);

        projector.Project(draft, def).Should().BeNull(
            "a module querying without the published predicate must still not be able to serve a draft");
    }
}
