using Xunit;
using FluentAssertions;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using barakoCMS.Models;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace BarakoCMS.Tests.Features.Public;

/// <summary>
/// Public delivery is opt-in per content type.
///
/// It used to be opt-out: <c>GET /api/public/{type}</c> served any content type as long as the
/// content was Published and its sensitivity Public — and both of those are the defaults, for
/// documents and for fields. So modelling members, orders or a ledger as content handed you an
/// anonymous endpoint for them without anyone deciding to publish anything.
///
/// That is the wrong way round: publishing should be a decision, not what happens when nobody makes
/// one. It is also not hypothetical — on a live deployment it exposed a club's member roster and its
/// chart of accounts to unauthenticated callers.
///
/// A type must now say so. Field-level sensitivity still applies on top; this is a gate above it.
/// </summary>
[Collection("Sequential")]
public class PublicDeliveryOptInTests
{
    private readonly IntegrationTestFixture _factory;
    private readonly HttpClient _anon;

    public PublicDeliveryOptInTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _anon = factory.CreateClient();
    }

    /// <summary>Seeds a type plus one Published, Public document carrying a findable marker.</summary>
    private async Task<(string type, string marker, string slug)> SeedAsync(bool deliverable)
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var type = $"probe{tag}";
        var marker = $"MARKER-{tag}";
        var slug = $"s-{tag}";

        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        using var s = store.LightweightSession();

        s.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = type,
            DisplayName = "Probe",
            IsPubliclyDeliverable = deliverable,
            Fields = new List<FieldDefinition>
            {
                new() { Name = "Title", Type = "string", Sensitivity = SensitivityLevel.Public },
                new() { Name = "Slug", Type = "slug", Sensitivity = SensitivityLevel.Public },
            },
        });

        s.Store(new Content
        {
            Id = Guid.NewGuid(),
            ContentType = type,
            Status = ContentStatus.Published,
            Sensitivity = SensitivityLevel.Public,
            Data = new Dictionary<string, object> { ["Title"] = marker, ["Slug"] = slug },
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await s.SaveChangesAsync();

        return (type, marker, slug);
    }

    [Fact]
    public async Task A_type_that_has_not_opted_in_is_not_listed()
    {
        var (type, marker, slug) = await SeedAsync(deliverable: false);

        var res = await _anon.GetAsync($"/api/public/{type}");
        var body = await res.Content.ReadAsStringAsync();

        // 404 rather than an empty 200: an empty list still confirms the type exists, and the whole
        // point is that a type nobody published should be invisible.
        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
        body.Should().NotContain(marker);
    }

    [Fact]
    public async Task A_type_that_has_opted_in_is_listed_exactly_as_before()
    {
        var (type, marker, slug) = await SeedAsync(deliverable: true);

        var res = await _anon.GetAsync($"/api/public/{type}");
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("totalItems").GetInt32().Should().Be(1);
        (await res.Content.ReadAsStringAsync()).Should().Contain(marker,
            "opting in must deliver the same content it always did");

        var bySlug = await _anon.GetAsync($"/api/public/{type}/{slug}");
        bySlug.StatusCode.Should().Be(HttpStatusCode.OK, "the slug route works again once opted in");
    }

    [Fact]
    public async Task The_gate_covers_search_and_the_slug_route_too()
    {
        var (type, marker, slug) = await SeedAsync(deliverable: false);

        // One endpoint left ungated is the whole hole: they read from the same documents. The slug
        // request uses the real seeded slug — asking for one that does not exist would 404 for the
        // ordinary reason and never reach the gate.
        //
        // The one-character query is deliberate too: search answered the short-query case with 200
        // before checking eligibility, which confirmed the type existed.
        foreach (var url in new[]
                 {
                     $"/api/public/{type}/search?q=MARKER",
                     $"/api/public/{type}/search?q=x",
                     $"/api/public/{type}/{slug}",
                 })
        {
            var res = await _anon.GetAsync(url);
            res.StatusCode.Should().Be(HttpStatusCode.NotFound, $"{url} should refuse a type that has not opted in");
            (await res.Content.ReadAsStringAsync()).Should().NotContain(marker, $"{url} must not deliver it");
        }
    }

    [Fact]
    public async Task The_feed_covers_it_too()
    {
        var (type, marker, slug) = await SeedAsync(deliverable: false);

        var res = await _anon.GetAsync($"/api/public/{type}/feed.xml");
        res.StatusCode.Should().Be(HttpStatusCode.NotFound, "an RSS feed is public delivery in another format");
        (await res.Content.ReadAsStringAsync()).Should().NotContain(marker);
    }

    [Fact]
    public async Task Opting_in_does_not_override_field_sensitivity()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var type = $"mixed{tag}";

        using (var scope = _factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
            using var s = store.LightweightSession();
            s.Store(new ContentTypeDefinition
            {
                Id = Guid.NewGuid(), Name = type, DisplayName = "Mixed",
                IsPubliclyDeliverable = true,
                Fields = new List<FieldDefinition>
                {
                    new() { Name = "Title", Type = "string", Sensitivity = SensitivityLevel.Public },
                    new() { Name = "Email", Type = "email", Sensitivity = SensitivityLevel.Sensitive },
                },
            });
            s.Store(new Content
            {
                Id = Guid.NewGuid(), ContentType = type,
                Status = ContentStatus.Published, Sensitivity = SensitivityLevel.Public,
                Data = new Dictionary<string, object>
                {
                    ["Title"] = $"public-{tag}",
                    ["Email"] = $"SECRET-{tag}@example.com",
                },
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            await s.SaveChangesAsync();
        }

        var body = await (await _anon.GetAsync($"/api/public/{type}")).Content.ReadAsStringAsync();

        // The new gate sits above field sensitivity, it does not replace it. A type being publishable
        // must never imply every field on it is.
        body.Should().Contain($"public-{tag}");
        body.Should().NotContain($"SECRET-{tag}", "a Sensitive field stays masked on a deliverable type");
    }

    [Fact]
    public async Task An_admin_can_turn_delivery_on_and_off_again()
    {
        var (type, marker, slug) = await SeedAsync(deliverable: false);

        var admin = _factory.CreateClient();
        admin.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", await _factory.StoredUserTokenAsync("Admin"));

        // Without this the opt-in would be a one-way door: content types have no update endpoint, so
        // on upgrade every existing type stops being delivered with no supported way back.
        var on = await admin.PutAsJsonAsync($"/api/content-types/{type}/public-delivery", new { enabled = true });
        on.IsSuccessStatusCode.Should().BeTrue(await on.Content.ReadAsStringAsync());

        var listed = await _anon.GetAsync($"/api/public/{type}");
        listed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await listed.Content.ReadAsStringAsync()).Should().Contain(marker);

        var off = await admin.PutAsJsonAsync($"/api/content-types/{type}/public-delivery", new { enabled = false });
        off.IsSuccessStatusCode.Should().BeTrue();
        (await _anon.GetAsync($"/api/public/{type}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Turning_delivery_on_requires_an_admin()
    {
        var (type, _, _) = await SeedAsync(deliverable: false);

        // Anonymous, and a signed-in Editor, must both be refused — otherwise the gate is decorative.
        (await _anon.PutAsJsonAsync($"/api/content-types/{type}/public-delivery", new { enabled = true }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var editor = _factory.CreateClient();
        editor.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", await _factory.StoredUserTokenAsync("Editor"));
        (await editor.PutAsJsonAsync($"/api/content-types/{type}/public-delivery", new { enabled = true }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        (await _anon.GetAsync($"/api/public/{type}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_type_with_no_definition_is_still_refused()
    {
        // The pre-existing fail-closed behaviour for an unknown type must survive the change.
        var res = await _anon.GetAsync($"/api/public/nosuchtype{Guid.NewGuid():N}");
        res.IsSuccessStatusCode.Should().BeFalse();
    }
}
