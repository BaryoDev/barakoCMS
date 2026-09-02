using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.ContentTypes;

/// <summary>
/// PUT /api/content-types/{name}/public-delivery leaves a trail, and says how much it exposed.
/// </summary>
/// <remarks>
/// This switch serves every published entry of a type to anonymous callers at once, and recorded
/// nothing. It is the larger of the pair with the field sensitivity change from #163, which is
/// audited, and it is the decision a reviewer asks about six months later.
///
/// The count is the part that makes an entry useful rather than merely present. "Public delivery
/// enabled" and "public delivery enabled, 4,000 entries now anonymous" are different sentences to
/// whoever reads the trail, and only one of them answers the question they came with.
/// </remarks>
[Collection("Sequential")]
public class PublicDeliveryAuditTests
{
    private readonly IntegrationTestFixture _factory;

    public PublicDeliveryAuditTests(IntegrationTestFixture factory) => _factory = factory;

    [Fact]
    public async Task Both_directions_are_recorded_with_the_actor_and_the_count()
    {
        var (type, _) = await SeedAsync(published: 3, drafts: 2);
        var client = Client();

        (await SetAsync(client, type, true)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await SetAsync(client, type, false)).StatusCode.Should().Be(HttpStatusCode.OK);

        var entries = await AuditEntriesAsync(type);

        entries.Should().HaveCount(2, "both directions are audited, or the trail says nothing happened");

        var enabled = entries.Single(e => e.Action == "contenttype.publicdelivery.enabled");
        enabled.Metadata!["publishedEntries"].ToString().Should().Be("3",
            "the number of entries this made anonymous is the point of the entry");
        enabled.TargetType.Should().Be("ContentType");
        enabled.ActorUserId.Should().NotBeNull("an unattributed exposure is not an audit trail");

        var disabled = entries.Single(e => e.Action == "contenttype.publicdelivery.disabled");
        disabled.Metadata!["contentType"].ToString().Should().Be(type);
    }

    /// <summary>
    /// The count is of published entries, not of every entry.
    /// </summary>
    /// <remarks>
    /// A draft is not served to anonymous callers whatever this setting says. Counting all of them
    /// overstates the exposure, and a number that overstates is one nobody trusts the second time.
    /// The seed has more drafts than published entries so the two cannot be confused.
    /// </remarks>
    [Fact]
    public async Task The_count_is_of_published_entries_only()
    {
        var (type, _) = await SeedAsync(published: 2, drafts: 5);

        (await SetAsync(Client(), type, true)).StatusCode.Should().Be(HttpStatusCode.OK);

        var enabled = (await AuditEntriesAsync(type))
            .Single(e => e.Action == "contenttype.publicdelivery.enabled");

        enabled.Metadata!["publishedEntries"].ToString().Should().Be("2",
            "five drafts stay invisible to anonymous callers, so counting seven would be a fiction");
    }

    /// <summary>
    /// A request that changes nothing records nothing.
    /// </summary>
    /// <remarks>
    /// A trail that logs a repeat as a change makes the entries that were changes harder to find,
    /// which is the opposite of what it is for. Paired with the assertion that the first one did
    /// land, so an endpoint that audits nothing at all cannot pass this.
    /// </remarks>
    [Fact]
    public async Task Setting_it_to_what_it_already_is_records_nothing()
    {
        var (type, _) = await SeedAsync(published: 1, drafts: 0);
        var client = Client();

        (await SetAsync(client, type, true)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await AuditEntriesAsync(type)).Should().HaveCount(1, "the first request changed something");

        (await SetAsync(client, type, true)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await AuditEntriesAsync(type)).Should().HaveCount(1, "the second changed nothing");
    }

    [Fact]
    public async Task The_response_says_how_many_entries_it_affected()
    {
        var (type, _) = await SeedAsync(published: 4, drafts: 1);

        var res = await SetAsync(Client(), type, true);
        var body = await res.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();

        body.GetProperty("publishedEntries").GetInt32().Should().Be(4,
            "so the admin can say what happened rather than only that something did");
    }

    /// <summary>
    /// With the acknowledgement required, enabling without it is refused and the message names the
    /// count.
    /// </summary>
    /// <remarks>
    /// Off by default, and this is why the default is off rather than on. This endpoint is the
    /// documented way back from the opt-in migration that stopped delivering every existing type, so
    /// a default that refuses the request until clients are updated turns the recovery path into a
    /// second outage. CLAUDE.md section 3 says the same thing generally: a new flag must not switch
    /// off something that used to work.
    /// </remarks>
    [Fact]
    public async Task Enabling_is_refused_without_the_acknowledgement_when_it_is_required()
    {
        var host = _factory.WithSetting("PublicDelivery:RequireAcknowledgement", "true");
        var (type, _) = await SeedAsync(published: 6, drafts: 0);

        var client = Client(host);
        var res = await SetAsync(client, type, true);
        var body = await res.Content.ReadAsStringAsync();

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, "got {0}: {1}", res.StatusCode, body);
        body.Should().Contain("6", "the refusal has to say how much is about to be exposed");

        (await AuditEntriesAsync(type)).Should().BeEmpty("refused means nothing happened");
        (await IsDeliverableAsync(type)).Should().BeFalse();

        // The control, in the same test: with the acknowledgement it goes through, so a host that
        // refused every request would not pass this.
        var acknowledged = await SetAsync(client, type, true, acknowledge: true);
        acknowledged.StatusCode.Should().Be(HttpStatusCode.OK,
            "got {0}: {1}", acknowledged.StatusCode, await acknowledged.Content.ReadAsStringAsync());

        (await IsDeliverableAsync(type)).Should().BeTrue();
    }

    /// <summary>
    /// Turning it off never needs the acknowledgement, whatever the setting says.
    /// </summary>
    /// <remarks>
    /// Disabling reduces exposure. Asking somebody to confirm the safe direction trains them to
    /// confirm without reading, which is how the confirmation on the unsafe direction stops working.
    /// </remarks>
    [Fact]
    public async Task Turning_it_off_never_needs_the_acknowledgement()
    {
        var host = _factory.WithSetting("PublicDelivery:RequireAcknowledgement", "true");
        var (type, _) = await SeedAsync(published: 2, drafts: 0, deliverable: true);

        var res = await SetAsync(Client(host), type, false);

        res.StatusCode.Should().Be(HttpStatusCode.OK,
            "got {0}: {1}", res.StatusCode, await res.Content.ReadAsStringAsync());
        (await IsDeliverableAsync(type)).Should().BeFalse();
    }

    /// <summary>
    /// By default nothing is required, which is what this endpoint has always done.
    /// </summary>
    [Fact]
    public async Task Enabling_needs_no_acknowledgement_by_default()
    {
        var (type, _) = await SeedAsync(published: 2, drafts: 0);

        (await SetAsync(Client(), type, true)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await IsDeliverableAsync(type)).Should().BeTrue();
    }

    private HttpClient Client(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>? host = null)
    {
        var client = (host ?? (Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>)_factory).CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            // Minted through the fixture rather than by signing in: /api/auth/* is rate limited to
            // five requests per fifteen minutes per IP, shared across the whole suite.
            _factory.CreateToken(["Admin"], Guid.NewGuid().ToString()));
        return client;
    }

    private static Task<HttpResponseMessage> SetAsync(
        HttpClient client, string type, bool enabled, bool acknowledge = false) =>
        client.PutAsJsonAsync($"/api/content-types/{type}/public-delivery",
            new { enabled, acknowledgeExposure = acknowledge });

    private async Task<(string Type, Guid DefinitionId)> SeedAsync(int published, int drafts, bool deliverable = false)
    {
        var type = "pd" + Guid.NewGuid().ToString("n")[..10];

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var def = new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = type,
            DisplayName = "Public delivery subject",
            Fields = [new FieldDefinition { Name = "Title", Type = "string" }],
            IsPubliclyDeliverable = deliverable,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        session.Store(def);

        for (var i = 0; i < published + drafts; i++)
        {
            session.Store(new barakoCMS.Models.Content
            {
                Id = Guid.NewGuid(),
                ContentType = type,
                Status = i < published ? ContentStatus.Published : ContentStatus.Draft,
                Data = new Dictionary<string, object> { ["Title"] = $"entry {i}" },
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
        }

        await session.SaveChangesAsync();
        return (type, def.Id);
    }

    private async Task<List<AuditEvent>> AuditEntriesAsync(string type)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();

        return (await session.Query<AuditEvent>()
                .Where(e => e.Action.StartsWith("contenttype.publicdelivery"))
                .ToListAsync())
            .Where(e => e.Metadata != null && e.Metadata["contentType"].ToString() == type)
            .ToList();
    }

    private async Task<bool> IsDeliverableAsync(string type)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var def = await session.Query<ContentTypeDefinition>().FirstOrDefaultAsync(d => d.Name == type);
        return def!.IsPubliclyDeliverable;
    }
}
