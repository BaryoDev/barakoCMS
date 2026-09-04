using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// The near filter and the distance it reports, against a real database.
/// </summary>
/// <remarks>
/// Three published entries at known coordinates and one Draft inside the radius. The numbers are
/// checked against an independent haversine in the test, within one percent, so a wrong radius
/// constant or a degrees-for-radians slip shows up as a number rather than as an ordering that
/// happens to look right.
/// </remarks>
[Collection("Sequential")]
public class DeliveryProximityIntegrationTests
{
    private readonly IntegrationTestFixture _factory;
    private readonly HttpClient _client; // anonymous

    public DeliveryProximityIntegrationTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private const double KoronadalLat = 6.5031, KoronadalLng = 124.8469;
    private const double GenSanLat = 6.1164, GenSanLng = 125.1716;
    private const double ManilaLat = 14.5995, ManilaLng = 120.9842;

    private async Task SeedAsync(string type)
    {
        using var scope = _factory.Services.CreateScope();
        var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        s.Store(new ContentTypeDefinition
        {
            IsPubliclyDeliverable = true,
            Id = Guid.NewGuid(),
            Name = type,
            DisplayName = type,
            Fields = new()
            {
                new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string" },
                new FieldDefinition { Name = "Location", DisplayName = "Location", Type = "geopoint" },
            },
        });

        void Add(string title, double lat, double lng, ContentStatus status) =>
            s.Store(new Content
            {
                Id = Guid.NewGuid(), ContentType = type, Status = status, Sensitivity = SensitivityLevel.Public,
                Data = new()
                {
                    ["Title"] = title,
                    ["Location"] = new Dictionary<string, object> { ["lat"] = lat, ["lng"] = lng },
                },
            });

        Add("koronadal", KoronadalLat, KoronadalLng, ContentStatus.Published);
        Add("gensan", GenSanLat, GenSanLng, ContentStatus.Published);
        Add("manila", ManilaLat, ManilaLng, ContentStatus.Published);

        // Two kilometres from the centre and never delivered.
        Add("draft-nearby", KoronadalLat + 0.018, KoronadalLng, ContentStatus.Draft);

        await s.SaveChangesAsync();
    }

    private sealed record Item(Dictionary<string, JsonElement> Data, double? DistanceKm);
    private sealed record Page(List<Item> Items, int TotalItems);

    private async Task<Page> PageAsync(string url)
    {
        var res = await _client.GetAsync(url);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        return (await res.Content.ReadFromJsonAsync<Page>())!;
    }

    private static string Title(Item i) => i.Data["Title"].GetString()!;

    private static string Near(double lat, double lng, double radius) =>
        $"filter[Location][near]={lat},{lng},{radius}";

    [Fact]
    public async Task Entries_within_the_radius_come_back_nearest_first_with_their_distance()
    {
        var type = "near_basic";
        await SeedAsync(type);

        var page = await PageAsync($"/api/public/{type}?{Near(KoronadalLat, KoronadalLng, 60)}&sort=distance");

        page.Items.Select(Title).Should().Equal(["koronadal", "gensan"],
            "the two Mindanao entries are within 60 km and Manila is about 1000 km away");
        page.TotalItems.Should().Be(2, "the query itself must exclude Manila and the Draft, not the projection");

        // Against an independent haversine, within one percent. A wrong Earth radius or a
        // degrees-for-radians mistake is well outside that.
        var expectedGenSan = Haversine(KoronadalLat, KoronadalLng, GenSanLat, GenSanLng);
        page.Items[0].DistanceKm.Should().Be(0);
        page.Items[1].DistanceKm.Should().NotBeNull();
        page.Items[1].DistanceKm!.Value.Should().BeApproximately(expectedGenSan, expectedGenSan * 0.01);
        expectedGenSan.Should().BeInRange(50, 60, "the fixture only proves anything if General Santos is inside the radius");
    }

    [Fact]
    public async Task Distance_is_reported_to_two_decimals()
    {
        var type = "near_decimals";
        await SeedAsync(type);

        var page = await PageAsync($"/api/public/{type}?{Near(KoronadalLat, KoronadalLng, 60)}");

        page.Items.Should().HaveCount(2);
        foreach (var item in page.Items)
        {
            item.DistanceKm.Should().NotBeNull();
            (Math.Round(item.DistanceKm!.Value, 2) - item.DistanceKm!.Value).Should().Be(0,
                "the value is rounded before it is sent, not left to the client");
        }
    }

    [Fact]
    public async Task Descending_distance_puts_the_farthest_first()
    {
        var type = "near_desc";
        await SeedAsync(type);

        var page = await PageAsync($"/api/public/{type}?{Near(KoronadalLat, KoronadalLng, 60)}&sort=-distance");

        page.Items.Select(Title).Should().Equal(["gensan", "koronadal"]);
    }

    /// <summary>
    /// The default cap reaches Manila from Koronadal, 994 km away, and the order still comes from
    /// the distance rather than from the insertion order the fixture happens to use.
    /// </summary>
    [Fact]
    public async Task A_wider_radius_reaches_the_far_entry_and_keeps_the_order()
    {
        var type = "near_wide";
        await SeedAsync(type);

        var page = await PageAsync($"/api/public/{type}?{Near(KoronadalLat, KoronadalLng, 1000)}&sort=distance");

        page.Items.Select(Title).Should().Equal(["koronadal", "gensan", "manila"]);
        var expectedManila = Haversine(KoronadalLat, KoronadalLng, ManilaLat, ManilaLng);
        expectedManila.Should().BeInRange(990, 1000, "the fixture only proves anything if Manila is just inside the cap");
        page.Items[2].DistanceKm!.Value.Should().BeApproximately(expectedManila, expectedManila * 0.01);
    }

    /// <summary>
    /// The reason this lives in core. The proximity test composes with the published predicate; it
    /// cannot replace it.
    /// </summary>
    [Fact]
    public async Task A_draft_inside_the_radius_is_absent()
    {
        var type = "near_draft";
        await SeedAsync(type);

        var page = await PageAsync($"/api/public/{type}?{Near(KoronadalLat, KoronadalLng, 5)}");

        page.Items.Select(Title).Should().Equal(["koronadal"]);
        page.Items.Select(Title).Should().NotContain("draft-nearby", "two kilometres away and still a Draft");
        page.TotalItems.Should().Be(1, "the count comes from the query, before projection, so it sees what the database was asked");
    }

    [Fact]
    public async Task Without_a_near_filter_no_distance_is_reported()
    {
        var type = "near_absent";
        await SeedAsync(type);

        var res = await _client.GetAsync($"/api/public/{type}");
        var raw = await res.Content.ReadAsStringAsync();

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        raw.Should().NotContain("distanceKm", "the key is absent rather than null when nothing was measured");
    }

    /// <summary>
    /// An entry whose point is not a point is skipped rather than failing the request.
    /// </summary>
    /// <remarks>
    /// Validation stops this on the write path, but a field can be retyped after entries exist. A
    /// cast error would take the whole list down for every caller because of one bad row.
    /// </remarks>
    [Fact]
    public async Task An_entry_with_a_malformed_point_is_skipped_rather_than_failing_the_request()
    {
        var type = "near_badrow";
        await SeedAsync(type);
        using (var scope = _factory.Services.CreateScope())
        {
            var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            s.Store(new Content
            {
                Id = Guid.NewGuid(), ContentType = type, Status = ContentStatus.Published,
                Sensitivity = SensitivityLevel.Public,
                Data = new() { ["Title"] = "stringy", ["Location"] = "6.5031,124.8469" },
            });
            s.Store(new Content
            {
                Id = Guid.NewGuid(), ContentType = type, Status = ContentStatus.Published,
                Sensitivity = SensitivityLevel.Public,
                Data = new() { ["Title"] = "unplaced" },
            });
            await s.SaveChangesAsync();
        }

        var page = await PageAsync($"/api/public/{type}?{Near(KoronadalLat, KoronadalLng, 60)}&sort=distance");

        page.Items.Select(Title).Should().Equal(["koronadal", "gensan"]);
    }

    [Theory]
    [InlineData("6.5031,124.8469")]
    [InlineData("north,124.8469,60")]
    [InlineData("6.5031,124.8469,0")]
    [InlineData("6.5031,124.8469,-10")]
    [InlineData("6.5031,124.8469,1001")]
    public async Task A_malformed_centre_or_radius_is_400(string value)
    {
        var type = "near_bad_" + Math.Abs(value.GetHashCode());
        await SeedAsync(type);

        var res = await _client.GetAsync($"/api/public/{type}?filter[Location][near]={Uri.EscapeDataString(value)}");

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync()).Should().Contain("near", "the 400 says which filter was refused");
    }

    [Fact]
    public async Task Sort_by_distance_without_a_near_filter_is_400()
    {
        var type = "near_sortonly";
        await SeedAsync(type);

        var res = await _client.GetAsync($"/api/public/{type}?sort=distance");

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, "there is no centre to measure from");
    }

    [Fact]
    public async Task A_near_filter_on_a_field_that_is_not_a_geopoint_is_400()
    {
        var type = "near_wrongtype";
        await SeedAsync(type);

        var res = await _client.GetAsync($"/api/public/{type}?filter[Title][near]=6.5031,124.8469,60");

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// The configured cap is read. A deployment that sets it to 10 km refuses the 60 km request
    /// the default accepts.
    /// </summary>
    [Fact]
    public async Task The_radius_cap_comes_from_configuration()
    {
        var type = "near_cap";
        await SeedAsync(type);

        // Not disposed: the derived factory shares the fixture's server.
        var capped = _factory.WithSettings(new Dictionary<string, string?> { ["Delivery:MaxRadiusKm"] = "10" });
        var client = capped.CreateClient();

        var refused = await client.GetAsync($"/api/public/{type}?{Near(KoronadalLat, KoronadalLng, 60)}");
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var allowed = await client.GetAsync($"/api/public/{type}?{Near(KoronadalLat, KoronadalLng, 10)}");
        allowed.StatusCode.Should().Be(HttpStatusCode.OK, "the control: 10 km is at the configured cap");
    }

    /// <summary>
    /// Writing through the API validates the point. A string is refused before it is stored.
    /// </summary>
    [Fact]
    public async Task Writing_a_string_into_a_geopoint_field_is_refused()
    {
        var type = "near_write";
        await SeedAsync(type);
        var admin = _factory.CreateClient();
        admin.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", await _factory.StoredUserTokenAsync("SuperAdmin", "Admin"));

        var bad = await admin.PostAsJsonAsync("/api/contents", new
        {
            contentType = type,
            data = new Dictionary<string, object> { ["Title"] = "x", ["Location"] = "6.5031,124.8469" },
        });
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest, await bad.Content.ReadAsStringAsync());

        var good = await admin.PostAsJsonAsync("/api/contents", new
        {
            contentType = type,
            data = new Dictionary<string, object>
            {
                ["Title"] = "x",
                ["Location"] = new { lat = KoronadalLat, lng = KoronadalLng },
            },
        });
        good.IsSuccessStatusCode.Should().BeTrue("the control: a real point is accepted ({0})", await good.Content.ReadAsStringAsync());
    }

    // Independent of the code under test: the textbook haversine on the mean Earth radius.
    private static double Haversine(double lat1, double lng1, double lat2, double lng2)
    {
        const double r = 6371.0088;
        double Rad(double d) => d * Math.PI / 180;
        var a = Math.Pow(Math.Sin(Rad(lat2 - lat1) / 2), 2)
              + Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) * Math.Pow(Math.Sin(Rad(lng2 - lng1) / 2), 2);
        return 2 * r * Math.Asin(Math.Sqrt(a));
    }
}
