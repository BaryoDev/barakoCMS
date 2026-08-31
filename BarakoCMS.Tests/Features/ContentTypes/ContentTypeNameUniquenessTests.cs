using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Models;
using Xunit;

namespace BarakoCMS.Tests.Features.ContentTypes;

/// <summary>
/// A content type's name is a lookup key: the validator, the sensitivity service and the search-text
/// backfill all resolve a definition by it, and each resolved a duplicate differently. Uniqueness
/// used to be a read followed by a write with nothing in the database behind it, so two requests
/// close enough together both read nothing and both inserted.
/// </summary>
/// <remarks>
/// Driven with two sessions saving at the same time rather than one after the other. A sequential
/// pair only ever exercises the read, which was never the broken half. See issue #198.
/// </remarks>
[Collection("Sequential")]
public class ContentTypeNameUniquenessTests
{
    private readonly IntegrationTestFixture _fixture;
    private readonly HttpClient _client;

    public ContentTypeNameUniquenessTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.CreateClient();
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", fixture.CreateToken(roles: new[] { "Admin", "SuperAdmin" }));
    }

    private static ContentTypeDefinition Definition(string name) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        DisplayName = name,
        Fields = new List<FieldDefinition>
        {
            new() { Name = "Title", DisplayName = "Title", Type = "string" },
        },
    };

    private static async Task<bool> SavedAsync(IDocumentSession session)
    {
        try
        {
            await session.SaveChangesAsync();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    [Fact]
    public async Task Two_concurrent_writes_of_one_name_leave_one_definition()
    {
        var name = "race-" + Guid.NewGuid().ToString("n")[..12];

        using var first = _fixture.Services.CreateScope();
        using var second = _fixture.Services.CreateScope();
        var a = first.ServiceProvider.GetRequiredService<IDocumentSession>();
        var b = second.ServiceProvider.GetRequiredService<IDocumentSession>();

        a.Store(Definition(name));
        b.Store(Definition(name));

        var outcomes = await Task.WhenAll(SavedAsync(a), SavedAsync(b));

        outcomes.Count(saved => saved).Should().Be(1,
            "the database has to refuse one of two concurrent writers, not both and not neither");

        using var check = _fixture.Services.CreateScope();
        var session = check.ServiceProvider.GetRequiredService<IQuerySession>();
        (await session.Query<ContentTypeDefinition>().Where(d => d.Name == name).CountAsync())
            .Should().Be(1);
    }

    /// <summary>
    /// The positive control. A uniqueness rule that refused every write would pass the test above.
    /// </summary>
    [Fact]
    public async Task Two_concurrent_writes_of_different_names_both_land()
    {
        var run = Guid.NewGuid().ToString("n")[..12];

        using var first = _fixture.Services.CreateScope();
        using var second = _fixture.Services.CreateScope();
        var a = first.ServiceProvider.GetRequiredService<IDocumentSession>();
        var b = second.ServiceProvider.GetRequiredService<IDocumentSession>();

        a.Store(Definition("race-a-" + run));
        b.Store(Definition("race-b-" + run));

        var outcomes = await Task.WhenAll(SavedAsync(a), SavedAsync(b));

        outcomes.Should().AllBeEquivalentTo(true, "different names do not conflict");
    }

    /// <summary>
    /// The constraint is per tenant. Under conjoined tenancy a global one would let one customer's
    /// "article" block every other customer's.
    /// </summary>
    [Fact]
    public async Task The_same_name_in_another_tenant_is_allowed()
    {
        var name = "shared-" + Guid.NewGuid().ToString("n")[..12];
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();

        await using (var session = store.LightweightSession())
        {
            session.Store(Definition(name));
            await session.SaveChangesAsync();
        }

        await using (var session = store.LightweightSession("uniqueness-club"))
        {
            session.Store(Definition(name));
            await session.SaveChangesAsync();
        }

        await using var check = store.QuerySession("uniqueness-club");
        (await check.Query<ContentTypeDefinition>().Where(d => d.Name == name).CountAsync())
            .Should().Be(1, "the tenant's own copy is there and is the only one it can see");
    }

    /// <summary>
    /// The endpoint answers a duplicate name the same way whichever half catches it: the read before
    /// the write, or the constraint underneath it. Never two successes, and never the raw Postgres
    /// error as a 500.
    /// </summary>
    [Fact]
    public async Task Concurrent_creates_of_one_name_give_one_success_and_one_conflict()
    {
        var name = "api-race-" + Guid.NewGuid().ToString("n")[..12];
        var body = new
        {
            name,
            displayName = "Api Race",
            fields = new[] { new { name = "Title", type = "string" } },
        };

        var responses = await Task.WhenAll(
            _client.PostAsJsonAsync("/api/content-types", body),
            _client.PostAsJsonAsync("/api/content-types", body));

        responses.Count(r => r.IsSuccessStatusCode).Should().Be(1,
            "one create wins; got {0} and {1}", responses[0].StatusCode, responses[1].StatusCode);

        var loser = responses.Single(r => !r.IsSuccessStatusCode);
        loser.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await loser.Content.ReadAsStringAsync()).Should().Contain("already exists");

        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        (await session.Query<ContentTypeDefinition>().Where(d => d.Name == name).CountAsync())
            .Should().Be(1);
    }

    /// <summary>The positive control for the endpoint: a name nobody is using still creates.</summary>
    [Fact]
    public async Task A_create_with_a_free_name_still_succeeds()
    {
        var response = await _client.PostAsJsonAsync("/api/content-types", new
        {
            name = "free-" + Guid.NewGuid().ToString("n")[..12],
            displayName = "Free",
            fields = new[] { new { name = "Title", type = "string" } },
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
